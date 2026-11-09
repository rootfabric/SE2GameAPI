using SE2GameAPI.Game;
using SE2GameAPI.Game.Devices;
using SE2GameAPI.Redis;
using Keen.VRage.Core.Game.Systems;

var router = new CommandRouter(); var gateway = new RedisGateway(); var registry = new DeviceRegistry();
var door = new DeviceAdapter("door") { Complete = false };
var lamp = new DeviceAdapter("lamp");
registry.Devices.Add("door", door); registry.Devices.Add("lamp", lamp);
Work Command(string id, string epoch = "epoch", long? expiry = null) => new(new(Guid.NewGuid().ToString("N"), epoch, "grid", id, "enable", expiry ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 9000));
void Pump(int count = 100) { for (int i = 0; i < count; i++) router.Run(new Session(), "epoch", registry, gateway, new SceneBudget()); }
void Check(bool value, string message) { if (!value) throw new Exception(message); }

var opening = Command("door"); var second = Command("door"); var light = Command("lamp");
gateway.Queue.Enqueue(opening); gateway.Queue.Enqueue(second); gateway.Queue.Enqueue(light); Pump();
Check(light.Completion.Task.IsCompletedSuccessfully, "Slow door blocked lamp");
Check(!opening.Completion.Task.IsCompleted && door.Applied == 1, "Same target wrote before confirmation");
door.Complete = true; Pump();
Check(opening.Completion.Task.Result.Status == "confirmed" && second.Completion.Task.Result.Status == "confirmed", "Confirmation failed");
Check(door.Applied == 2 && router.Depth == 0, "Lane accounting failed");
var expired = Command("lamp", expiry: 0); var stale = Command("lamp", epoch: "old");
gateway.Queue.Enqueue(expired); gateway.Queue.Enqueue(stale); Pump();
Check(expired.Completion.Task.Result.Error == "expired" && stale.Completion.Task.Result.Error == "stale_session", "Invalid command admitted");
Check(lamp.Applied == 1, "Rejected command mutated device");
door.Complete = false;
var burst = Enumerable.Range(0, 20).Select(_ => Command("door")).ToArray();
foreach (var work in burst) gateway.Queue.Enqueue(work);
Pump();
Check(burst.Any(w => w.Completion.Task.IsCompletedSuccessfully && w.Completion.Task.Result.Error == "target_busy"), "No per-target backpressure");
Check(router.Depth <= 9, "Unbounded target queue");
router.Reset(); Check(router.Depth == 0 && burst.All(w => w.Completion.Task.IsCompleted), "Reset leaked requests");
var removed = Command("lamp"); lamp.Removed = true; gateway.Queue.Enqueue(removed); Pump();
Check(removed.Completion.Task.Result.Error == "target_changed", "Removed device was written");
Console.WriteLine("PASS: fairness, serialization, confirmation, expiry, epoch, bounded overload, reset, removal");

namespace Keen.VRage.Core.Game.Systems { internal sealed class Session { } }
namespace SE2GameAPI.Redis
{
    internal sealed class RedisGateway
    {
        public readonly Queue<Work> Queue = new();
        public bool TryRead(out Work? work) => Queue.TryDequeue(out work);
    }
}
namespace SE2GameAPI.Game
{
    internal sealed class DeviceRegistry { public readonly Dictionary<string, DeviceAdapter> Devices = new(); }
}
namespace SE2GameAPI.Game.Devices
{
    internal sealed class DeviceAdapter(string id)
    {
        public bool Complete = true, Removed;
        public int Applied;
        public DeviceState? Read(Session session) => Removed ? null : new(id, "grid", "Grid", id, id, true, true, true, 1);
        public Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before) { Applied++; return _ => Complete; }
    }
}
