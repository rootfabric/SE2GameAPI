using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;

internal sealed class InventoryDevice(FunctionalBlockComponent block, InventoryComponent inventory, string family,
    Dictionary<string, DeviceAdapter> registry) : DeviceAdapter
{
    private readonly FunctionalBlockComponent sourceBlock = block;
    private readonly InventoryComponent sourceInventory = inventory;
    private readonly DEntity entityId = block.DEntity;
    private DeviceState? state;
    private string? fingerprint;
    private InventoryProperties? properties;
    private static readonly string[] Features = ["sourceInventory.read", "sourceInventory.transfer"];
    private sealed record StackInfo(int Index, string Item, string Amount);
    private sealed record InventoryProperties(string MassCurrent, string MassMax, StackInfo[] Items);
    public override string Id => entityId.ToString();
    public override DeviceState? Read(Session session)
    {
        if (!session.Scene.IsEntityAlive(entityId) || sourceBlock.IsDestroyed || sourceBlock.Grid.GridClosing ||
            !session.IsEntityInScene(sourceBlock.Entity)) return null;
        var items = sourceInventory.Items;
        // Limit inspection in one tick; large inventories need a paged adapter later.
        if (items.Length > 128) throw new InvalidOperationException("inventory_too_large_for_snapshot");
        var result = new StackInfo[items.Length];
        for (int i = 0; i < items.Length; i++) result[i] = new(i, items[i].Definition.ToString() ?? "", items[i].Amount.ToString());
        var mass = sourceInventory.Mass;
        string key = mass.Current + "/" + mass.Max + "/" + string.Join(";", result.Select(x => x.Item + ":" + x.Amount));
        if (key != fingerprint)
        {
            properties = new(mass.Current.ToString(), mass.Max.ToString(), result);
            fingerprint = key;
        }
        var next = new DeviceState(Id, sourceBlock.Grid.DEntity.ToString(), sourceBlock.Grid.DisplayName.EvaluateText(),
            sourceBlock.DisplayName.EvaluateText(), family, true, sourceBlock.Working, sourceBlock.Functional,
            state?.Revision ?? 0, Status: "Inventory", Capabilities: Features, Properties: properties);
        next = next with { MeasuredAtMs = state?.MeasuredAtMs ?? 0 };
        if (next != state) next = next with { Revision = next.Revision + 1 };
        return state = next with { MeasuredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
    }
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (command.Cmd != "transfer_item") return null;
        if (command.TargetId == Id || !registry.TryGetValue(command.TargetId ?? "", out var target) || target is not InventoryDevice destination)
            throw new InvalidOperationException("invalid_target_inventory");
        if (destination.sourceBlock.Grid.DEntity.ToString() != before.GridId ||
            sourceInventory.ConveyorSystem == null || !ReferenceEquals(sourceInventory.ConveyorSystem, destination.sourceInventory.ConveyorSystem))
            throw new InvalidOperationException("inventories_not_connected");
        if (command.ItemIndex is not int index || index < 0 || index >= sourceInventory.Items.Length ||
            command.Amount is not double quantity || !double.IsFinite(quantity) || quantity <= 0)
            throw new InvalidOperationException("invalid_item_or_amount");
        var item = sourceInventory.Items[index];
        var amount = (Keen.VRage.Library.Mathematics.FixedPoint)quantity;
        if (amount <= 0 || sourceInventory.CountItem(item.Definition) < amount)
            throw new InvalidOperationException("insufficient_items");
        var sourceBefore = sourceInventory.CountItem(item.Definition);
        var targetBefore = destination.sourceInventory.CountItem(item.Definition);
        var moved = sourceInventory.TransferByIdx(destination.sourceInventory, index, null, amount, false);
        if (moved != amount) throw new InvalidOperationException("transfer_not_completed");
        return _ => sourceInventory.CountItem(item.Definition) == sourceBefore - amount &&
                    destination.sourceInventory.CountItem(item.Definition) == targetBefore + amount;
    }
}
