using System.Diagnostics;
using Keen.VRage.Core.Game.Systems;
using SE2GameAPI.Game.Devices;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game;
internal sealed class TelemetryScheduler
{
    private DeviceAdapter[]? cycle;
    private int index;
    private long nextSample;
    private readonly List<DeviceState> buffer = new();
    public DeviceState[] Snapshot { get; private set; } = [];
    public long SampledAtMs { get; private set; }
    public void Run(Session session, DeviceRegistry registry, SceneBudget budget)
    {
        if (cycle == null && Stopwatch.GetTimestamp() >= nextSample)
        {
            cycle = registry.Devices.Values.ToArray(); index = 0; buffer.Clear();
            nextSample = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        }
        int count = 0;
        while (cycle != null && index < cycle.Length && count++ < 64 && budget.HasTime)
        {
            var device = cycle[index++]; var value = device.Read(session);
            if (value != null) buffer.Add(value); else registry.Devices.Remove(device.Id);
        }
        if (cycle != null && index == cycle.Length)
        {
            Snapshot = buffer.ToArray(); cycle = null;
            SampledAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
