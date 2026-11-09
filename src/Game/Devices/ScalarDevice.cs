using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;

internal sealed class ScalarDevice(PowerableBlockComponent block, string family, string commandName,
    string propertyName, Func<float> read, Action<float> write, float minimum, float maximum) : FunctionalDevice(block)
{
    private readonly string[] features = ["switch.enabled", propertyName];
    private sealed record ScalarProperties(string Name, float Value, float Minimum, float Maximum);
    public override string Type => family;
    protected override string[] Capabilities => features;
    protected override object Properties => new ScalarProperties(propertyName, read(), minimum, maximum);
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (command.Cmd != commandName) return base.Apply(command, before);
        if (command.Amount is not double value || !double.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidOperationException("invalid_value");
        float requested = (float)value;
        write(requested);
        return actual => actual.Properties is ScalarProperties p && Math.Abs(p.Value - requested) < 0.0001f;
    }
}
