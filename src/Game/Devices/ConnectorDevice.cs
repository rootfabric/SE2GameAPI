using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Library.Mathematics;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;
internal sealed class ConnectorDevice(PowerableBlockComponent block, ConnectorComponent connector,
    MechanicalBlockCounterpartDetectionComponent? detector, Dictionary<string, DeviceAdapter> registry)
    : FunctionalDevice(block), IInventorySource
{
    private static readonly string[] Features = ["switch.enabled", "connector.connect", "inventory.transfer"];
    public override string Type => "connector";
    protected override string[] Capabilities => Features;
    public InventoryComponent? PrimaryInventory => Inventories.Length > 0 ? Inventories[0] : null;
    protected override string Status => connector.IsConnected ? "Connected" : "Disconnected";
    private sealed record ConnectorProperties(bool IsConnected, string? CounterpartId, string? NearbyCounterpartId,
        bool ReadyToConnect, bool Aligned,
        double[] PivotPosition, double[] PivotForward, double[] PivotUp);
    protected override object Properties
    {
        get
        {
            var nearby = detector?.TryGetCounterpart();
            // World-space mating pivot of the connector: docking approach targets this frame.
            var pivot = (MatrixD)connector.WorldSpacePivotTransform;
            bool aligned = nearby != null && connector.IsAlignedWellEnoughToConnectWith(nearby);
            return new ConnectorProperties(connector.IsConnected, connector.ConnectedEntity?.DEntity.ToString(),
                nearby?.DEntity.ToString(), !connector.IsConnected && Block.Enabled && Block.Working && nearby != null,
                aligned,
                [pivot.Translation.X, pivot.Translation.Y, pivot.Translation.Z],
                [pivot.Forward.X, pivot.Forward.Y, pivot.Forward.Z],
                [pivot.Up.X, pivot.Up.Y, pivot.Up.Z]);
        }
    }
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (command.Cmd == "transfer_item")
        {
            var inventory = PrimaryInventory ?? throw new InvalidOperationException("no_inventory");
            return InventoryTransfer.Apply(command, before, inventory, Id, before.GridId, registry);
        }
        if (command.Cmd is not ("connect" or "disconnect")) return base.Apply(command, before);
        if (!before.Enabled || !before.Working) throw new InvalidOperationException("connector_not_working");
        if (command.Cmd == "connect")
        {
            var nearby = detector?.TryGetCounterpart()
                ?? throw new InvalidOperationException("no_nearby_connector");
            // TryConnect is the engine's synchronous attempt: false means alignment limits failed.
            if (!connector.TryConnect(nearby)) throw new InvalidOperationException("connector_not_aligned");
        }
        else connector.RequestDisconnect();
        string wanted = command.Cmd == "connect" ? "Connected" : "Disconnected";
        return actual => actual.Status == wanted;
    }
}
