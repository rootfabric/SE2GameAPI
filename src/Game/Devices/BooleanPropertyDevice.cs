using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;

internal sealed class BooleanPropertyDevice(PowerableBlockComponent block, string family, string commandName,
    string propertyName, Func<bool> read, Action<bool> write) : FunctionalDevice(block)
{
    private readonly string[] features = ["switch.enabled", propertyName];
    private sealed record BooleanProperties(string Name, bool Value);
    public override string Type => family;
    protected override string[] Capabilities => features;
    protected override object Properties => new BooleanProperties(propertyName, read());
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (command.Cmd != commandName) return base.Apply(command, before);
        if (!command.Enabled.HasValue) throw new InvalidOperationException("missing_enabled");
        write(command.Enabled.Value);
        return actual => actual.Properties is BooleanProperties p && p.Value == command.Enabled.Value;
    }
}
