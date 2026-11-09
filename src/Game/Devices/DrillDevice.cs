using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Tools.Voxels;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;
internal sealed class DrillDevice(PowerableBlockComponent block, DrillComponent drill) : FunctionalDevice(block)
{
    private static readonly string[] Features = ["switch.enabled", "drill.mode"];
    public override string Type => "drill";
    protected override string[] Capabilities => Features;
    // Primary action collects ore; secondary action excavates a wider shape (extension).
    protected override string Status => drill.IsSecondaryActionEnabled ? "Extend"
        : drill.IsPrimaryActionEnabled ? "Collect" : "Idle";
    private sealed record DrillProperties(bool IsPrimaryActionEnabled, bool IsSecondaryActionEnabled,
        bool IsDrillingVoxels);
    protected override object Properties => new DrillProperties(drill.IsPrimaryActionEnabled,
        drill.IsSecondaryActionEnabled, drill.IsDrillingVoxels);
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (command.Cmd != "drill_mode") return base.Apply(command, before);
        if (!before.Enabled || !before.Working) throw new InvalidOperationException("drill_not_working");
        string wanted = command.Mode switch
        {
            "off" => "Idle", "collect" => "Collect", "extend" => "Extend",
            _ => throw new ArgumentException("invalid_drill_mode"),
        };
        // Switching actions: disable the other brush first, then enable the requested one.
        if (command.Mode == "off")
        {
            drill.TrySetPrimaryAction(false, false);
            drill.TrySetSecondaryAction(false, false);
        }
        else if (command.Mode == "collect")
        {
            drill.TrySetSecondaryAction(false, false);
            drill.TrySetPrimaryAction(true, false);
        }
        else
        {
            drill.TrySetPrimaryAction(false, false);
            drill.TrySetSecondaryAction(true, false);
        }
        return actual => actual.Status == wanted;
    }
}
