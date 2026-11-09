using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;

internal sealed class LandingGearDevice(PowerableBlockComponent block, MagneticBlockComponent gear) : FunctionalDevice(block)
{
    private static readonly string[] Features = ["switch.enabled", "landing_gear.lock", "landing_gear.auto_lock"];
    private sealed record GearProperties(bool Locked, bool ReadyToLock, bool AutoLock, string? LockedEntityId);
    public override string Type => "landing_gear";
    protected override string[] Capabilities => Features;
    protected override string Status => gear.IsLocked ? "Locked" : gear.ReadyToLockEntity != null ? "Ready" : "Unlocked";
    protected override object Properties => new GearProperties(gear.IsLocked, gear.ReadyToLockEntity != null,
        gear.AutoLock, gear.LockedEntity?.DEntity.ToString());
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        switch (command.Cmd)
        {
            case "lock":
                if (!before.Enabled || gear.ReadyToLockEntity == null) throw new InvalidOperationException("no_lock_target");
                gear.RequestLock(); return actual => actual.Status == "Locked";
            case "unlock":
                gear.RequestUnlock(); return actual => actual.Status != "Locked";
            case "set_auto_lock":
                if (!command.Enabled.HasValue) throw new InvalidOperationException("missing_enabled");
                gear.AutoLock = command.Enabled.Value;
                return actual => actual.Properties is GearProperties p && p.AutoLock == command.Enabled.Value;
            default: return base.Apply(command, before);
        }
    }
}
