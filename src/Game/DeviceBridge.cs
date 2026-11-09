using System.Diagnostics;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Utils;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game;

internal sealed class DeviceBridge(RedisGateway gateway, string runId) : IDisposable
{
    private DeviceRegistry registry = new();
    private TelemetryScheduler telemetry = new();
    private readonly CommandRouter router = new();
    private Session? session;
    private int generation, registryGeneration = -1;
    private long pendingToken, sequence, ticks;
    private volatile bool disposed;
    private double totalMs, maxMs;
    private string? error;
    private readonly long[] histogram = new long[9];
    private string Epoch => $"{runId}:{generation}";
    public void Poll()
    {
        if (disposed) return;
        var app = Singleton<VRageCore>.Instance.Engine.Components.FirstOrDefault(c => c.GetType().FullName == "Keen.Game2.GameAppComponent");
        var current = app?.GetType().GetProperty("ServerSession")?.GetValue(app) as Session;
        if (!ReferenceEquals(session, current))
        {
            session = current; generation++; Volatile.Write(ref pendingToken, 0);
        }
        if (current == null || current.IsUnloading)
        {
            gateway.Update(new(Epoch, "WAITING_FOR_WORLD", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], 0, maxMs, ticks == 0 ? 0 : totalMs / ticks, ticks, router.Applied, gateway.QueueDepth + router.Depth, registry.DiscoveryMs));
            while (gateway.TryRead(out var work)) work!.Completion.TrySetResult(new(work.Command.RequestId, "rejected", "no_world", null));
            return;
        }
        long token = Interlocked.Increment(ref sequence);
        if (Interlocked.CompareExchange(ref pendingToken, token, 0) != 0) return;
        _ = TickAsync(current, generation, token);
    }
    private async Task TickAsync(Session current, int expectedGeneration, long token)
    {
        try
        {
            await current.Scene.MoveToDCS();
            if (disposed || expectedGeneration != generation || current.IsUnloading) return;
            var budget = new SceneBudget();
            if (registryGeneration != expectedGeneration)
            {
                router.Reset(); registry = new(); telemetry = new();
                registryGeneration = expectedGeneration;
            }
            registry.Navigate(current, budget);
            // Rotate first service so a busy phase cannot starve discovery or telemetry.
            for (int phase = 0; phase < 3 && budget.HasTime; phase++)
            {
                switch ((ticks + phase) % 3)
                {
                    case 0: router.Run(current, Epoch, registry, gateway, budget); break;
                    case 1: registry.Discover(current, budget); break;
                    case 2: telemetry.Run(current, registry, budget); break;
                }
            }
            error = null;
            double elapsed = budget.ElapsedMs;
            ticks++; totalMs += elapsed; maxMs = Math.Max(maxMs, elapsed);
            int bucket = elapsed <= .1 ? 0 : elapsed <= .25 ? 1 : elapsed <= .5 ? 2 : elapsed <= 1 ? 3 : elapsed <= 2 ? 4 : elapsed <= 5 ? 5 : elapsed <= 10 ? 6 : elapsed <= 20 ? 7 : 8;
            histogram[bucket]++;
            gateway.Update(new(Epoch, telemetry.SampledAtMs == 0 ? "DISCOVERING" : "READY", telemetry.SampledAtMs, telemetry.Snapshot,
                elapsed, maxMs, totalMs / ticks, ticks, router.Applied, gateway.QueueDepth + router.Depth, registry.DiscoveryMs, error, (long[])histogram.Clone(), registry.Inventory, registry.InventoryAtMs));
        }
        catch (Exception e)
        {
            error = e.ToString();
            gateway.Update(new(Epoch, "ERROR", telemetry.SampledAtMs, telemetry.Snapshot,
                0, maxMs, ticks == 0 ? 0 : totalMs / ticks, ticks, router.Applied, gateway.QueueDepth + router.Depth, registry.DiscoveryMs, error));
        }
        finally { Interlocked.CompareExchange(ref pendingToken, 0, token); }
    }
    public void Dispose()
    {
        disposed = true;
        if (session is { IsUnloading: false } current) _ = StopNavigationAsync(current);
    }
    private async Task StopNavigationAsync(Session current)
    {
        try { await current.Scene.MoveToDCS(); if (!current.IsUnloading) registry.StopNavigation(current); }
        catch { /* Scene disposal owns the entities after unloading. */ }
    }
}
