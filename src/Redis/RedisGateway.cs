using System.Text.Json;
using System.Threading.Channels;
using StackExchange.Redis;
namespace SE2GameAPI.Redis;

// Background worker owns Redis, JSON and files. Game threads exchange plain immutable DTOs only.
internal sealed class RedisGateway : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly Channel<Work> input = Channel.CreateBounded<Work>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource stop = new();
    private readonly string directory;
    private readonly Task worker;
    private BridgeSnapshot snapshot = new("starting", "STARTING", 0, [], 0, 0, 0, 0, 0, 0, 0);
    private int depth;
    public int QueueDepth => Volatile.Read(ref depth);
    public RedisGateway(string directory) { this.directory = directory; worker = Task.Run(RunAsync); }
    public void Update(BridgeSnapshot value) => Volatile.Write(ref snapshot, value);
    public bool TryRead(out Work? work)
    {
        if (!input.Reader.TryRead(out work)) return false;
        Interlocked.Decrement(ref depth); return true;
    }
    private async Task RunAsync()
    {
        var pending = new Dictionary<string, Work>();
        var completed = new Dictionary<string, (string Json, long Expires)>();
        ConnectionMultiplexer? connection = null;
        string? lastError = null;
        long nextPublish = 0;
        long lastInventoryAtMs = 0;
        try
        {
            Directory.CreateDirectory(directory);
            var options = ConfigurationOptions.Parse("127.0.0.1:6379,abortConnect=false,connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000");
            options.BacklogPolicy = BacklogPolicy.FailFast;
            connection = await ConnectionMultiplexer.ConnectAsync(options);
            var db = connection.GetDatabase();
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var work in pending.Values)
                        if (!work.Completion.Task.IsCompleted && now > work.Command.ExpiresAtMs + 1000)
                            work.Completion.TrySetResult(new(work.Command.RequestId, "unknown", "execution_timeout", null));
                    foreach (var id in completed.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) completed.Remove(id);
                    foreach (var pair in pending.Where(p => p.Value.Completion.Task.IsCompletedSuccessfully).ToArray())
                    {
                        string json = JsonSerializer.Serialize(pair.Value.Completion.Task.Result, Json);
                        completed[pair.Key] = (json, now + 60000);
                        await db.StringSetAsync($"se2:local:reply:{pair.Key}", json, TimeSpan.FromSeconds(60));
                        pending.Remove(pair.Key);
                    }
                    int capacity = Math.Min(16, 64 - pending.Count);
                    if (capacity > 0)
                    {
                        var messages = await db.ListLeftPopAsync("se2:local:commands", capacity) ?? [];
                        foreach (var message in messages)
                        {
                            if (message.Length() > 4096) continue;
                            DeviceCommand? cmd;
                            try { cmd = JsonSerializer.Deserialize<DeviceCommand>((string)message!, Json); } catch { continue; }
                            if (cmd == null || !Guid.TryParseExact(cmd.RequestId, "N", out _)) continue;
                            if (completed.TryGetValue(cmd.RequestId, out var old))
                            {
                                await db.StringSetAsync($"se2:local:reply:{cmd.RequestId}", old.Json, TimeSpan.FromSeconds(60));
                                continue;
                            }
                            if (pending.ContainsKey(cmd.RequestId)) continue;
                            // Reserve deduplication space before execution; never silently stop polling.
                            if (completed.Count + pending.Count >= 4096)
                            {
                                var busy = JsonSerializer.Serialize(new CommandResult(cmd.RequestId, "rejected", "dedup_capacity", null), Json);
                                await db.StringSetAsync($"se2:local:reply:{cmd.RequestId}", busy, TimeSpan.FromSeconds(60));
                                continue;
                            }
                            var work = new Work(cmd);
                            pending.Add(cmd.RequestId, work);
                            if (cmd.ExpiresAtMs <= now || cmd.ExpiresAtMs > now + 10000)
                                work.Completion.TrySetResult(new(cmd.RequestId, "rejected", "invalid_deadline", null));
                            else
                            {
                                Interlocked.Increment(ref depth);
                                if (!input.Writer.TryWrite(work))
                                {
                                    Interlocked.Decrement(ref depth);
                                    work.Completion.TrySetResult(new(cmd.RequestId, "rejected", "busy", null));
                                }
                            }
                        }
                    }
                    if (now >= nextPublish)
                    {
                        nextPublish = now + 200;
                        string json = JsonSerializer.Serialize(Volatile.Read(ref snapshot), Json);
                        await db.StringSetAsync("se2:local:state", json, TimeSpan.FromSeconds(3));
                        File.WriteAllText(Path.Combine(directory, "bridge-state.json.tmp"), json);
                        File.Move(Path.Combine(directory, "bridge-state.json.tmp"), Path.Combine(directory, "bridge-state.json"), true);
                    }
                    var inventory = Volatile.Read(ref snapshot);
                    if (inventory.InventoryAtMs != 0 && inventory.InventoryAtMs != lastInventoryAtMs && inventory.Blocks != null)
                    {
                        string inventoryJson = JsonSerializer.Serialize(new { inventory.Epoch, inventory.InventoryAtMs, Blocks = inventory.Blocks }, Json);
                        await db.StringSetAsync("se2:local:inventory", inventoryJson, TimeSpan.FromMinutes(10));
                        File.WriteAllText(Path.Combine(directory, "block-inventory.json.tmp"), inventoryJson);
                        File.Move(Path.Combine(directory, "block-inventory.json.tmp"), Path.Combine(directory, "block-inventory.json"), true);
                        lastInventoryAtMs = inventory.InventoryAtMs;
                    }
                    lastError = null;
                    await Task.Delay(10, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    if (lastError != e.Message)
                    {
                        lastError = e.Message;
                        File.AppendAllText(Path.Combine(directory, "transport.log"), $"{DateTimeOffset.UtcNow:O} {e}\n");
                    }
                    await Task.Delay(500, stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { File.AppendAllText(Path.Combine(directory, "transport.log"), e + "\n"); }
        finally { if (connection != null) await connection.CloseAsync(); connection?.Dispose(); }
    }
    public void Dispose() { stop.Cancel(); input.Writer.TryComplete(); }
}
