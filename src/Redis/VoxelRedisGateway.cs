using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using SE2GameAPI.Game.Voxels;
using StackExchange.Redis;
namespace SE2GameAPI.Redis;

// Separate bounded transport: voxel payloads cannot delay ordinary device receipts.
internal sealed class VoxelRedisGateway : IVoxelTransport,IDisposable
{
    private const string Prefix="se2:local:voxels";
    private readonly Channel<VoxelRequest> requests=Channel.CreateBounded<VoxelRequest>(8);
    private readonly ConcurrentDictionary<string,VoxelRequest> cancellations=new();
    private readonly ConcurrentDictionary<string,long> cancelled=new();
    private readonly Channel<VoxelEvent> events=Channel.CreateBounded<VoxelEvent>(8);
    private readonly CancellationTokenSource stop=new();
    private sealed record SessionState(string Epoch,bool Ready);
    private SessionState session=new("starting",false);
    private readonly Task worker;
    public VoxelRedisGateway()=>worker=Task.Run(RunAsync);
    public void SetSession(string epoch,bool ready)=>Volatile.Write(ref session,new(epoch,ready));
    public bool TryRead(out VoxelRequest? value)
    {
        foreach(var id in cancellations.Keys)
            if(cancellations.TryRemove(id,out value)) return true;
        while(requests.Reader.TryRead(out value))
            if(!cancelled.ContainsKey(value.RequestId)) return true;
        value=null; return false;
    }
    public bool TryWrite(VoxelEvent value)=>events.Writer.TryWrite(value);
    private static bool Terminal(string state)=>state is "complete" or "partial" or "rejected" or "cancelled" or "failed";
    private static VoxelStatus Rejected(VoxelRequest r,string error,long now)=>new(
        r.RequestId,r.Epoch,r.GridId,"rejected",error,null,double.IsFinite(r.Radius)?r.Radius:0,0,0,0,0,0,[],now);
    private async Task RunAsync()
    {
        ConnectionMultiplexer? connection=null;
        // Request ids are reserved before admission, including failures. No silent duplicate scans.
        var pending=new Dictionary<string,VoxelRequest>();
        var recent=new Dictionary<string,long>();
        VoxelEvent? unsent=null;
        try
        {
            var options=ConfigurationOptions.Parse("127.0.0.1:6379,abortConnect=false,connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000");
            options.BacklogPolicy=BacklogPolicy.FailFast;
            connection=await ConnectionMultiplexer.ConnectAsync(options);
            var db=connection.GetDatabase();
            async Task Publish(VoxelEvent value)
            {
                var s=value.Status;
                // Idempotent tile keys make reconnect/retry safe; write tile BEFORE advertising it.
                if(value.Tile!=null)
                    await db.StringSetAsync($"{Prefix}:tile:{s.RequestId}:{value.Tile.Index}",
                        JsonSerializer.Serialize(value.Tile,RedisGateway.Json),TimeSpan.FromMinutes(10));
                var json=JsonSerializer.Serialize(s,RedisGateway.Json);
                await db.StringSetAsync($"{Prefix}:scan:{s.RequestId}",json,TimeSpan.FromMinutes(5));
                await db.PublishAsync(RedisChannel.Literal($"{Prefix}:events"),json);
            }
            while(!stop.IsCancellationRequested)
            {
                try
                {
                    long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var state=Volatile.Read(ref session);
                    foreach(var id in cancelled.Where(p=>p.Value<now).Select(p=>p.Key).ToArray()) cancelled.TryRemove(id,out _);
                    foreach(var id in recent.Where(p=>p.Value<now).Select(p=>p.Key).ToArray()) recent.Remove(id);
                    foreach(var item in pending.Values.Where(r=>!state.Ready || r.Epoch!=state.Epoch || r.ExpiresAtMs+1000<now).ToArray())
                    {
                        await Publish(new(Rejected(item,!state.Ready?"no_world":item.Epoch!=state.Epoch?"stale_session":"deadline",now)));
                        pending.Remove(item.RequestId);
                    }
                    if(unsent==null) events.Reader.TryRead(out unsent);
                    if(unsent!=null)
                    {
                        if(pending.ContainsKey(unsent.Status.RequestId) && unsent.Status.Epoch==state.Epoch && state.Ready)
                        {
                            await Publish(unsent);
                            if(Terminal(unsent.Status.State)) pending.Remove(unsent.Status.RequestId);
                        }
                        unsent=null;
                    }
                    // At most one request admitted per transport turn. Python enqueue is also bounded.
                    var raw=await db.ListLeftPopAsync($"{Prefix}:requests");
                    if(!raw.IsNull && raw.Length()<=4096)
                    {
                        VoxelRequest? r=null;
                        try { r=JsonSerializer.Deserialize<VoxelRequest>((string)raw!,RedisGateway.Json); } catch(JsonException) { }
                        if(r!=null && Guid.TryParseExact(r.RequestId,"N",out _))
                        {
                            if(r.Action=="cancel")
                            {
                                if(pending.TryGetValue(r.RequestId,out var running) && running.Epoch==r.Epoch)
                                {
                                    cancelled[r.RequestId]=running.ExpiresAtMs+1000;
                                    cancellations[r.RequestId]=r; // Priority control, not blocked by a full request channel.
                                }
                            }
                            else if(!recent.ContainsKey(r.RequestId))
                            {
                                if(recent.Count>=1024) await Publish(new(Rejected(r,"dedup_capacity",now)));
                                else
                                {
                                    recent.Add(r.RequestId,now+600000);
                                    string? error=!state.Ready?"no_world":r.Epoch!=state.Epoch?"stale_session":
                                        r.ExpiresAtMs<=now || r.ExpiresAtMs>now+120000?"invalid_deadline":pending.Count>=8?"busy":null;
                                    if(error!=null) await Publish(new(Rejected(r,error,now)));
                                    else
                                    {
                                        pending.Add(r.RequestId,r);
                                        // Enqueue before returning queued. A full channel is an explicit rejection.
                                        if(!requests.Writer.TryWrite(r))
                                        { pending.Remove(r.RequestId); await Publish(new(Rejected(r,"busy",now))); }
                                        else await Publish(new(new(r.RequestId,r.Epoch,r.GridId,"queued",null,null,r.Radius,0,0,0,0,0,[],now)));
                                    }
                                }
                            }
                        }
                    }
                    await Task.Delay(25,stop.Token);
                }
                catch(OperationCanceledException) when(stop.IsCancellationRequested) { break; }
                catch(Exception e)
                {
                    System.Diagnostics.Trace.WriteLine($"SE2 voxel transport: {e.Message}");
                    await Task.Delay(500,stop.Token);
                }
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(Exception e) { System.Diagnostics.Trace.WriteLine($"SE2 voxel transport stopped: {e}"); }
        finally { if(connection!=null) await connection.CloseAsync(); connection?.Dispose(); }
    }
    public void Dispose() { stop.Cancel(); requests.Writer.TryComplete(); events.Writer.TryComplete(); }
}
