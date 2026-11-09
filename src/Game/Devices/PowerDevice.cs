using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.ItemConverters;
using Keen.Game2.Simulation.WorldObjects.Weapons;
namespace SE2GameAPI.Game.Devices;

// Common switch for powered blocks that have no specialized adapter yet.
internal sealed class PowerDevice(PowerableBlockComponent block, string family) : FunctionalDevice(block)
{
    private readonly SeatComponent? seat = block.Entity.TryGet<SeatComponent>();
    private readonly ItemConverterComponent? converter = block.Entity.TryGet<ItemConverterComponent>();
    private readonly ProjectileWeaponComponent? weapon = block.Entity.TryGet<ProjectileWeaponComponent>();
    private sealed record Details(bool? HasPilot, string? PilotId, bool? Crafting, bool? ConstantShooting);
    public override string Type => family;
    protected override object? Properties => seat == null && converter == null && weapon == null ? null :
        new Details(seat?.HasPilot, seat?.Pilot?.DEntity.ToString(), converter?.Crafting, weapon?.ConstantShooting);
}
