using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Shared.Visuals.Lights;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;
internal sealed class LampDevice(PowerableBlockComponent block, LightComponent light) : FunctionalDevice(block)
{
    public override string Type => "lamp";
    private static readonly string[] Features = ["switch.enabled", "light.brightness"];
    private sealed record LightProperties(float Brightness);
    protected override string[] Capabilities => Features;
    protected override object Properties => new LightProperties(light.LightBrightness);
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (command.Cmd != "set_brightness") return base.Apply(command, before);
        if (command.Amount is not double value || !double.IsFinite(value) || value < 0 || value > 10)
            throw new InvalidOperationException("invalid_brightness");
        light.LightBrightness = (float)value;
        return actual => actual.Properties is LightProperties p && Math.Abs(p.Brightness - value) < 0.0001;
    }
}
