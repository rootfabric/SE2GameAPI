using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Doors;
using Keen.Game2.Simulation.WorldObjects.Shared.Visuals.Lights;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;
using Keen.Game2.Simulation.WorldObjects.Tools.Voxels;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.ItemConverters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
namespace SE2GameAPI.Game.Devices;
internal static class DeviceFactory
{
    public static DeviceAdapter? Create(CubeBlockComponent block, Dictionary<string, DeviceAdapter> registry)
    {
        if (block.Definition.DebugNameShort.StartsWith("CargoContainer", StringComparison.Ordinal))
        {
            var cargoFunctional = block.Entity.Components.OfType<FunctionalBlockComponent>().FirstOrDefault();
            var cargoInventory = block.Entity.Components.OfType<InventoryComponent>().FirstOrDefault();
            if (cargoFunctional != null && cargoInventory != null)
                return new InventoryDevice(cargoFunctional, cargoInventory, "cargo", registry);
        }
        var power = block.Entity.TryGet<PowerableBlockComponent>();
        if (power == null)
        {
            var functional = block.Entity.Components.OfType<FunctionalBlockComponent>().FirstOrDefault();
            if (functional == null) return null;
            string name = block.Definition.DebugNameShort;
            if (name.StartsWith("Conveyor", StringComparison.Ordinal)) return new PassiveDevice(functional, "conveyor");
            if (name.StartsWith("SolarPanel", StringComparison.Ordinal)) return new PassiveDevice(functional, "solar");
            if (name.StartsWith("HydrogenTank", StringComparison.Ordinal)) return new PassiveDevice(functional, "tank",
                block.Entity.TryGet<ResourceContainerComponent>());
            return null;
        }
        var door = block.Entity.TryGet<DoorServerComponent>();
        if (door != null) return new DoorDevice(power, door);
        var light = block.Entity.TryGet<LightComponent>();
        if (light != null) return new LampDevice(power, light);
        var connector = block.Entity.TryGet<ConnectorComponent>();
        if (connector != null) return new ConnectorDevice(power, connector,
            block.Entity.TryGet<MechanicalBlockCounterpartDetectionComponent>(), registry);
        var gear = block.Entity.TryGet<MagneticBlockComponent>();
        if (gear != null) return new LandingGearDevice(power, gear);
        var drill = block.Entity.TryGet<DrillComponent>();
        if (drill != null) return new DrillDevice(power, drill);
        var thruster = block.Entity.TryGet<ThrusterComponent>();
        if (thruster != null) return new ScalarDevice(power, "thruster", "set_thrust_override",
            "thruster.override", () => thruster.ThrustOverride, v => thruster.ThrustOverride = v, 0, 1);
        var gravity = block.Entity.TryGet<GravityGeneratorComponent>();
        if (gravity != null) return new ScalarDevice(power, "gravity_generator", "set_gravity",
            "gravity.acceleration", () => gravity.GravitationalAcceleration,
            gravity.SetGravitationalAcceleration, -9.81f, 9.81f);
        var antenna = block.Entity.TryGet<AntennaComponent>();
        if (antenna != null) return new BooleanPropertyDevice(power, "antenna", "set_broadcast",
            "antenna.broadcast", () => antenna.BroadcastCustomMessage, v => antenna.BroadcastCustomMessage = v);
        var converter = block.Entity.TryGet<ItemToResourceConverterComponent>();
        if (converter != null) return new BooleanPropertyDevice(power, block.Definition.DebugNameShort.Split('_')[0].StartsWith("Reactor") ? "Reactor" : "O2H2Generator", "set_processing",
            "converter.processing", () => converter.IsProcessingResources, v => converter.IsProcessingResources = v);
        var resource = block.Entity.TryGet<ResourceContainerComponent>();
        if (resource != null && block.Definition.DebugNameShort.StartsWith("Battery", StringComparison.Ordinal))
            return new ResourcePowerDevice(power, "Battery", resource);
        string definition = block.Definition.DebugNameShort;
        string family = definition.Split('_')[0];
        foreach (string prefix in new[] { "IonThruster", "Gyroscope", "LandingGear", "Battery", "Reactor", "Assembler", "Refinery", "Smelter", "Antenna", "GravityGenerator", "Cockpit", "ControlSeat", "Button", "Gatling", "MedicalRoom", "SurvivalKit", "O2H2Generator", "Bed", "Drill", "OreDetector" })
            if (family.StartsWith(prefix, StringComparison.Ordinal)) return new PowerDevice(power, prefix);
        return null;
    }
}
