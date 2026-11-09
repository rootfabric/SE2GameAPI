using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using SE2GameAPI.Redis;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
namespace SE2GameAPI.Game.Devices;

internal sealed class PassiveDevice(FunctionalBlockComponent block, string family,
    ResourceContainerComponent? resource = null) : DeviceAdapter
{
    private readonly DEntity id = block.DEntity;
    private DeviceState? state;
    private static readonly string[] Features = ["state.read"];
    private sealed record ResourceProperties(string Current, string Maximum, string Flow, string Mode);
    public override string Id => id.ToString();
    public override DeviceState? Read(Session session)
    {
        if (!session.Scene.IsEntityAlive(id) || block.IsDestroyed || block.Grid.GridClosing ||
            !session.IsEntityInScene(block.Entity)) return null;
        var next = new DeviceState(Id, block.Grid.DEntity.ToString(), block.Grid.DisplayName.EvaluateText(),
            block.DisplayName.EvaluateText(), family, false, block.Working, block.Functional,
            state?.Revision ?? 0, Capabilities: resource == null ? Features : ResourceFeatures,
            Properties: resource == null ? null : new ResourceProperties(
                resource.CurrentChargeValue.ToString(), resource.MaxChargeValue.ToString(),
                resource.CurrentChargeFlowRate.ToString(), resource.Mode.ToString()));
        next = next with { MeasuredAtMs = state?.MeasuredAtMs ?? 0 };
        if (next != state) next = next with { Revision = next.Revision + 1 };
        return state = next with { MeasuredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
    }
    private static readonly string[] ResourceFeatures = ["state.read", "resource.charge", "resource.mode"];
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (resource == null || command.Cmd != "set_mode") return null;
        if (!Enum.TryParse<ContainerMode>(command.Mode, true, out var mode) ||
            mode is not (ContainerMode.Auto or ContainerMode.Charge or ContainerMode.Discharge))
            throw new InvalidOperationException("invalid_resource_mode");
        resource.Mode = mode;
        return actual => actual.Properties is ResourceProperties p && p.Mode == mode.ToString();
    }
}
