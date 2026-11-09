using System.Diagnostics;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using SE2GameAPI.Redis;
using Keen.Game2.Simulation.WorldObjects.Items;
namespace SE2GameAPI.Game.Devices;

// Scene-thread-only adapters: no transport, timers or serialization.
internal abstract class FunctionalDevice(PowerableBlockComponent block) : DeviceAdapter
{
    protected readonly PowerableBlockComponent Block = block;
    public DEntity EntityId { get; } = block.DEntity;
    public override string Id { get; } = block.DEntity.ToString();
    public DeviceState? State { get; private set; }
    private long nextMetadata;
    private readonly InventoryComponent[] inventories = block.Entity.Components.OfType<InventoryComponent>().ToArray();
    protected InventoryComponent[] Inventories => inventories;
    private InventoryTelemetry[]? inventorySnapshot;
    private string? inventoryFingerprint;
    public abstract string Type { get; }
    protected virtual string? Status => null;
    protected virtual object? Properties => null;
    protected virtual string[] Capabilities => SwitchCapabilities;
    private static readonly string[] SwitchCapabilities = ["switch.enabled"];
    public override DeviceState? Read(Session session)
    {
        if (!session.Scene.IsEntityAlive(EntityId) || Block.IsDestroyed ||
            !session.IsEntityInScene(Block.Entity) || Block.Grid.GridClosing) return null;
        var value = State;
        long now = Stopwatch.GetTimestamp();
        if (value == null || now >= nextMetadata || value.GridId != Block.Grid.DEntity.ToString())
        {
            value = new(Id, Block.Grid.DEntity.ToString(), Block.Grid.DisplayName.EvaluateText(),
                Block.DisplayName.EvaluateText(), Type, Block.Enabled, Block.Working, Block.Functional,
                State?.Revision ?? 0, Status: Status, Capabilities: Capabilities);
            nextMetadata = now + 5 * Stopwatch.Frequency;
        }
        else value = value with { Enabled = Block.Enabled, Working = Block.Working, Functional = Block.Functional, Status = Status };
        value = value with { MeasuredAtMs = State?.MeasuredAtMs ?? 0, Properties = Properties,
            Inventories = ReadInventories() };
        if (value != State) value = value with { Revision = value.Revision + 1 };
        return State = value with { MeasuredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
    }
    private InventoryTelemetry[]? ReadInventories()
    {
        if (inventories.Length == 0) return null;
        var result = new InventoryTelemetry[inventories.Length];
        for (int slot = 0; slot < inventories.Length; slot++)
        {
            var inventory = inventories[slot]; var stacks = inventory.Items;
            // Avoid an unbounded scene-thread read. A paged API can handle larger cases.
            if (stacks.Length > 128) return inventorySnapshot;
            var items = new InventoryItemTelemetry[stacks.Length];
            for (int index = 0; index < stacks.Length; index++)
                items[index] = new(index, stacks[index].Definition.ToString() ?? "", stacks[index].Amount.ToString());
            var mass = inventory.Mass;
            result[slot] = new(slot, mass.Current.ToString(), mass.Max.ToString(), items);
        }
        string fingerprint = string.Join("|", result.Select(r => r.MassCurrent + "/" + r.MassMaximum + ":" +
            string.Join(";", r.Items.Select(i => i.Definition + "=" + i.Amount))));
        if (fingerprint != inventoryFingerprint)
        {
            inventoryFingerprint = fingerprint; inventorySnapshot = result;
        }
        return inventorySnapshot;
    }
    public override Func<DeviceState, bool>? Apply(DeviceCommand command, DeviceState before)
    {
        bool? desired = command.Cmd switch {
            "enable" or "on" or "start" => true, "disable" or "off" or "stop" => false,
            "set_enabled" => command.Enabled, "toggle" => !before.Enabled, _ => null
        };
        if (!desired.HasValue) return null;
        Block.Enabled = desired.Value;
        return actual => actual.Enabled == desired.Value;
    }
}
