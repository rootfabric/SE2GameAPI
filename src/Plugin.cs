using Keen.VRage.Core;
using Keen.VRage.Core.Plugins;
using Keen.VRage.Library.Utils;
using SE2GameAPI.Game;
using SE2GameAPI.Redis;
namespace SE2GameAPI;
public sealed class Plugin : IPlugin, IDisposable
{
    private readonly RedisGateway gateway;
    private readonly VoxelRedisGateway voxelGateway;
    private readonly BuildRedisGateway buildGateway;
    private readonly DeviceBridge bridge;
    private readonly Timer timer;
    private int pending;
    private volatile bool disposed;
    public Plugin()
    {
        gateway = new RedisGateway(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SE2GameAPI"));
        voxelGateway = new VoxelRedisGateway();
        buildGateway = new BuildRedisGateway();
        bridge = new DeviceBridge(gateway, Guid.NewGuid().ToString("N"), voxelGateway, buildGateway);
        timer = new Timer(_ => Schedule(), null, 1000, 50);
    }
    private void Schedule()
    {
        if (disposed || Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
        try
        {
            Singleton<VRageCore>.Instance.UpdateQueue.Enqueue(() =>
            {
                try { if (!disposed) bridge.Poll(); }
                catch { }
                finally { Interlocked.Exchange(ref pending, 0); }
            });
        }
        catch { Interlocked.Exchange(ref pending, 0); }
    }
    public void Dispose() { disposed = true; timer.Dispose(); bridge.Dispose(); voxelGateway.Dispose(); buildGateway.Dispose(); gateway.Dispose(); }
}
