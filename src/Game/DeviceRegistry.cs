using System.Diagnostics;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using SE2GameAPI.Game.Devices;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game;

internal sealed class DeviceRegistry
{
    private sealed class GridCursor(CubeGridComponent grid)
    {
        public readonly CubeGridComponent Grid = grid;
        public readonly DEntity Id = grid.DEntity;
        public int Version = -1, Index;
        public string Name = grid.DisplayName.EvaluateText();
        public readonly AutopilotDevice Autopilot = new(grid);
    }
    public readonly Dictionary<string, DeviceAdapter> Devices = new();
    private readonly Dictionary<string, BlockInventory> inventory = new();
    public BlockInventory[] Inventory { get; private set; } = [];
    public long InventoryAtMs { get; private set; }
    private GridCursor[] grids = [];
    private long nextGrids;
    private int gridIndex;
    private bool inventoryDirty = true;
    private int navigationIndex;
    public double DiscoveryMs { get; private set; }
    public void Discover(Session session, SceneBudget budget)
    {
        long started = Stopwatch.GetTimestamp();
        if (started >= nextGrids)
        {
            // Only the grid list is materialized. Never retain an engine query iterator across frames.
            var previous = grids.ToDictionary(g => g.Id);
            grids = session.GetEntitiesOfType<CubeGridComponent>()
                .Select(e => e.TryGet<CubeGridComponent>()).Where(g => g != null && !g.GridClosing)
                .Select(g => previous.TryGetValue(g!.DEntity, out var cursor) ? cursor : new GridCursor(g)).ToArray();
            foreach (var cursor in grids)
            {
                Devices.TryAdd(cursor.Autopilot.Id, cursor.Autopilot);
                cursor.Autopilot.CanStart = () => grids.Count(g => g.Autopilot.Active) < 2;
            }
            var active = grids.Select(g => g.Id.ToString()).ToHashSet();
            foreach (var id in inventory.Where(p => !active.Contains(p.Value.GridId)).Select(p => p.Key).ToArray()) inventory.Remove(id);
            inventoryDirty = true;
            nextGrids = started + 5 * Stopwatch.Frequency;
        }
        int visited = 0, blocks = 0;
        while (visited++ < grids.Length && budget.HasTime && blocks < 128)
        {
            if (gridIndex >= grids.Length) gridIndex = 0;
            var cursor = grids[gridIndex++];
            if (!session.Scene.IsEntityAlive(cursor.Id) || cursor.Grid.GridClosing) continue;
            var storage = cursor.Grid.CubeBlockStorage;
            if (cursor.Version != storage.Version)
            {
                cursor.Index = 0; cursor.Version = storage.Version;
                cursor.Autopilot.ResetBlocks();
                cursor.Name = cursor.Grid.DisplayName.EvaluateText();
                foreach (var id in inventory.Where(p => p.Value.GridId == cursor.Id.ToString()).Select(p => p.Key).ToArray()) inventory.Remove(id);
                inventoryDirty = true;
            }
            var entries = storage.GetAllCubeBlocks();
            // A span exists only inside this call; version changes restart the cursor next time.
            int slice = 0;
            while (cursor.Index < entries.Length && blocks < 128 && slice++ < 32 && budget.HasTime)
            {
                var block = entries[cursor.Index++]; blocks++;
                if (block == null || block.IsDestroyed) continue;
                string id = block.DEntity.ToString();
                cursor.Autopilot.AddBlock(block);
                if (!inventory.ContainsKey(id))
                {
                    var components = block.Entity.Components.Select(c => c.GetType().FullName ?? c.GetType().Name).ToArray();
                    inventory.Add(id, new(cursor.Id.ToString(), cursor.Name, id,
                        block.Definition.DebugNameShort, components,
                        block.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Doors.DoorServerComponent>() != null ? "door" :
                        block.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Visuals.Lights.LightComponent>() != null ? "lamp" : null));
                    inventoryDirty = true;
                }
                if (Devices.ContainsKey(id)) continue;
                var adapter = DeviceFactory.Create(block, Devices);
                if (adapter != null) Devices.Add(id, adapter);
            }
            cursor.Autopilot.Ready = cursor.Index >= entries.Length;
        }
        // Publish a stable inventory only after every grid cursor reaches its end.
        if (inventoryDirty && grids.Length > 0 && grids.All(g => g.Index >= g.Grid.CubeBlockStorage.GetAllCubeBlocks().Length))
        {
            Inventory = inventory.Values.ToArray();
            InventoryAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            inventoryDirty = false;
        }
        DiscoveryMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    public void Navigate(Session session, SceneBudget budget)
    {
        if (grids.Length == 0) return;
        int first = navigationIndex++ % grids.Length;
        for (int i = 0; i < grids.Length; i++)
        {
            if (!budget.HasTime) break;
            grids[(first+i)%grids.Length].Autopilot.Tick(session);
        }
    }
    public void StopNavigation(Session session)
    {
        foreach (var cursor in grids) cursor.Autopilot.Shutdown(session);
    }
}
