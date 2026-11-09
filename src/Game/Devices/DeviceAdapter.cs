using Keen.VRage.Core.Game.Systems;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;

// Non-switch devices can implement this contract without inheriting power semantics.
internal abstract class DeviceAdapter
{
    public abstract string Id { get; }
    public abstract DeviceState? Read(Session session);
    // Apply returns an observation predicate; the shared router owns polling and deadlines.
    public abstract Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before);
}
