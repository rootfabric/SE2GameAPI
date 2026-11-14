using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using StackExchange.Redis;
using SE2GameAPI.Game.Constructions;

namespace SE2GameAPI.Redis;

// Bounded transport for construction requests (projections): the DCS tick
// never blocks on Redis I/O; a worker thread owns the connection.
internal sealed class BuildRedisGateway : IDisposable
{
    private const string Prefix = "se2:local:build";
    private readonly Channel<ProjectionRequest> requests = Channel.CreateBounded<ProjectionRequest>(4);
    private readonly ConcurrentDictionary<string, long> recent = new();
    private readonly Channel<ProjectionStatus> replies = Channel.CreateBounded<ProjectionStatus>(8);
    private readonly CancellationTokenSource stop = new();
    private sealed record SessionState(string Epoch, bool Ready);
    private SessionState session = new("starting", false);
    private readonly Task worker;
    public BuildRedisGateway() => worker = Task.Run(RunAsync);
    public void SetSession(string epoch, bool ready) => Volatile.Write(ref session, new(epoch, ready));
    public bool TryRead(out ProjectionRequest? value) => requests.Reader.TryRead(out value);
    public bool TryWrite(ProjectionStatus value) => replies.Writer.TryWrite(value);

    private async Task RunAsync()
    {
        ConnectionMultiplexer? connection = null;
        var pending = new HashSet<string>();
        ProjectionStatus? unsent = null;
        try
        {
            var options = ConfigurationOptions.Parse("127.0.0.1:6379,abortConnect=false,connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000");
            options.BacklogPolicy = BacklogPolicy.FailFast;
            connection = await ConnectionMultiplexer.ConnectAsync(options);
            var db = connection.GetDatabase();
            async Task Publish(ProjectionStatus value)
            {
                var json = JsonSerializer.Serialize(value, RedisGateway.Json);
                await db.StringSetAsync($"{Prefix}:{value.RequestId}", json, TimeSpan.FromMinutes(5));
                await db.PublishAsync(RedisChannel.Literal($"{Prefix}:events"), json);
            }
            ProjectionStatus Rejected(ProjectionRequest r, string error, long now) =>
                new(r.RequestId, r.Epoch, r.GridId, "rejected", error, null, 0, 0, now);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var state = Volatile.Read(ref session);
                    foreach (var id in recent.Where(p => p.Value < now).Select(p => p.Key).ToArray())
                        if (recent.TryRemove(id, out _)) pending.Remove(id);
                    if (unsent == null) replies.Reader.TryRead(out unsent);
                    if (unsent != null)
                    {
                        if (pending.Contains(unsent.RequestId) && unsent.Epoch == state.Epoch && state.Ready)
                        {
                            await Publish(unsent);
                            if (unsent.State is "complete" or "failed" or "rejected") pending.Remove(unsent.RequestId);
                        }
                        unsent = null;
                    }
                    var raw = await db.ListLeftPopAsync($"{Prefix}:requests");
                    if (!raw.IsNull && raw.Length() <= 4096)
                    {
                        ProjectionRequest? r = null;
                        try { r = JsonSerializer.Deserialize<ProjectionRequest>((string)raw!, RedisGateway.Json); } catch (JsonException) { }
                        if (r != null && Guid.TryParseExact(r.RequestId, "N", out _) && !recent.ContainsKey(r.RequestId))
                        {
                            if (recent.Count >= 256) await Publish(Rejected(r, "dedup_capacity", now));
                            else
                            {
                                recent[r.RequestId] = now + 600000;
                                string? error = !state.Ready ? "no_world" : r.Epoch != state.Epoch ? "stale_session" :
                                    r.ExpiresAtMs <= now || r.ExpiresAtMs > now + 120000 ? "invalid_deadline" :
                                    pending.Count >= 4 ? "busy" :
                                    r.Action is not ("create" or "list" or "materialize" or "convert_block" or "create_template") ? "invalid_action" :
                                    string.IsNullOrWhiteSpace(r.GridId) ? "grid_required" : null;
                                if (error != null) await Publish(Rejected(r, error, now));
                                else
                                {
                                    pending.Add(r.RequestId);
                                    if (!requests.Writer.TryWrite(r))
                                    {
                                        pending.Remove(r.RequestId);
                                        await Publish(Rejected(r, "busy", now));
                                    }
                                    else
                                        await Publish(new(r.RequestId, r.Epoch, r.GridId, "queued", null, null, 0, 0, now));
                                }
                            }
                        }
                    }
                    await Task.Delay(25, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    System.Diagnostics.Trace.WriteLine($"SE2 build transport: {e.Message}");
                    await Task.Delay(500, stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { System.Diagnostics.Trace.WriteLine($"SE2 build transport stopped: {e}"); }
        finally { if (connection != null) await connection.CloseAsync(); connection?.Dispose(); }
    }
    public void Dispose() { stop.Cancel(); requests.Writer.TryComplete(); replies.Writer.TryComplete(); }
}
