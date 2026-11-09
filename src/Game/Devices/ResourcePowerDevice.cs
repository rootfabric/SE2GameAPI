using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;

internal sealed class ResourcePowerDevice(PowerableBlockComponent block, string family,
    ResourceContainerComponent resource) : FunctionalDevice(block)
{
    private static readonly string[] Features = ["switch.enabled", "resource.charge", "resource.mode"];
    private sealed record ResourceProperties(string Current, string Maximum, string Flow, string Mode);
    public override string Type => family;
    protected override string[] Capabilities => Features;
    protected override object Properties => new ResourceProperties(resource.CurrentChargeValue.ToString(),
        resource.MaxChargeValue.ToString(), resource.CurrentChargeFlowRate.ToString(), resource.Mode.ToString());
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (command.Cmd != "set_mode") return base.Apply(command, before);
        if (!Enum.TryParse<ContainerMode>(command.Mode, true, out var mode) ||
            mode is not (ContainerMode.Auto or ContainerMode.Charge or ContainerMode.Discharge))
            throw new InvalidOperationException("invalid_resource_mode");
        resource.Mode = mode;
        return actual => actual.Properties is ResourceProperties p && p.Mode == mode.ToString();
    }
}
