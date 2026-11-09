using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game.Devices;

// Any device with an inventory (cargo, connector, ...) can act as a transfer source/target.
internal interface IInventorySource
{
    public InventoryComponent? PrimaryInventory { get; }
}

// Shared transfer_item handling for inventory-bearing devices.
internal static class InventoryTransfer
{
    public static Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before,
        InventoryComponent source, string sourceDeviceId, string sourceGridId,
        Dictionary<string, DeviceAdapter> registry)
    {
        if (command.Cmd != "transfer_item") return null;
        if (command.TargetId == sourceDeviceId ||
            !registry.TryGetValue(command.TargetId ?? "", out var target) || target is not IInventorySource destination ||
            destination.PrimaryInventory is not { } destinationInventory)
            throw new InvalidOperationException("invalid_target_inventory");
        if (destination is DeviceAdapter destAdapter && destAdapter.Id == sourceDeviceId)
            throw new InvalidOperationException("invalid_target_inventory");
        // The engine's TransferByIdx enforces inventory filters itself; SE2 keeps separate
        // conveyor-system objects per grid, so an explicit connectivity check would wrongly
        // reject docked-grid transfers.
        if (command.ItemIndex is not int index || index < 0 || index >= source.Items.Length ||
            command.Amount is not double quantity || !double.IsFinite(quantity) || quantity <= 0)
            throw new InvalidOperationException("invalid_item_or_amount");
        var item = source.Items[index];
        var amount = (Keen.VRage.Library.Mathematics.FixedPoint)quantity;
        if (amount <= 0 || source.CountItem(item.Definition) < amount)
            throw new InvalidOperationException("insufficient_items");
        var sourceBefore = source.CountItem(item.Definition);
        var targetBefore = destinationInventory.CountItem(item.Definition);
        var moved = source.TransferByIdx(destinationInventory, index, null, amount, false);
        if (moved != amount) throw new InvalidOperationException("transfer_not_completed");
        return _ => source.CountItem(item.Definition) == sourceBefore - amount &&
                    destinationInventory.CountItem(item.Definition) == targetBefore + amount;
    }
}

internal sealed class InventoryDevice(FunctionalBlockComponent block, InventoryComponent inventory, string family,
    Dictionary<string, DeviceAdapter> registry) : DeviceAdapter, IInventorySource
{
    private readonly FunctionalBlockComponent sourceBlock = block;
    private readonly InventoryComponent sourceInventory = inventory;
    public InventoryComponent? PrimaryInventory => sourceInventory;
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
        return InventoryTransfer.Apply(command, before, sourceInventory, Id, before.GridId, registry);
    }
}
