using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Doors;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;
internal sealed class DoorDevice(PowerableBlockComponent block, DoorServerComponent door) : FunctionalDevice(block)
{
    private static readonly string[] DoorCapabilities = ["switch.enabled", "door.open"];
    public override string Type => "door";
    protected override string[] Capabilities => DoorCapabilities;
    protected override string Status => door.DoorState.ToString();
    private sealed record DoorProperties(string Status, bool IsOpen);
    protected override object Properties => new DoorProperties(Status, Status == "Open");
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        bool? open = command.Cmd switch {
            "open" or "open_door" => true, "close" or "close_door" => false,
            "toggle_door" => before.Status != "Open" && before.Status != "Opening", _ => null
        };
        if (!open.HasValue) return base.Apply(command, before);
        if (!before.Enabled || !before.Working) throw new InvalidOperationException("door_not_working");
        door.ToggleOpenState(open.Value);
        string desired = open.Value ? "Open" : "Closed";
        return actual => actual.Status == desired;
    }
}
