using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Utils;

namespace SE2GameAPI.Game;

internal sealed class GridDiscovery(string directory, string runId) : IDisposable
{
    private readonly object gate = new();
    private Session? activeSession;
    private bool pending;
    private bool disposed;
    private int generation;
    private string? lastState;
    private readonly Dictionary<string, string> previous = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Called from VRageCore.UpdateQueue; GameAppComponent is internal in this game build.
    public void Poll()
    {
        var engine = Singleton<VRageCore>.Instance.Engine;
        var app = engine.Components.FirstOrDefault(c => c.GetType().FullName == "Keen.Game2.GameAppComponent");
        var session = app?.GetType().GetProperty("ServerSession", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(app) as Session;
        lock (gate)
        {
            if (disposed) return;
            if (!ReferenceEquals(session, activeSession))
            {
                activeSession = session;
                generation++;
                pending = false;
                previous.Clear();
                lastState = null;
            }
            if (session == null || session.IsUnloading)
            {
                PublishState(app == null ? "GAME_APP_UNAVAILABLE" : "WAITING_FOR_SERVER_WORLD");
                return;
            }
            if (pending) return;
            pending = true;
            _ = ScanAsync(session, generation);
        }
    }

    private async Task ScanAsync(Session session, int scanGeneration)
    {
        try
        {
            // Session entity queries require the scene's DCS thread, not merely the app main thread.
            await session.Scene.MoveToDCS();
            lock (gate)
            {
                if (disposed || scanGeneration != generation || session.IsUnloading) return;
            }
            var watch = Stopwatch.StartNew();
            var grids = new List<GridInfo>();
            foreach (var entity in session.GetEntitiesOfType<CubeGridComponent>())
            {
                var grid = entity.Get<CubeGridComponent>();
                if (grid.GridClosing) continue;
                var devices = new List<DeviceInfo>();
                var blockTypes = new Dictionary<string, int>(StringComparer.Ordinal);
                int blockCount = 0;
                foreach (var block in grid.CubeBlockStorage.GetAllCubeBlocks())
                {
                    if (block == null || block.IsDestroyed) continue;
                    blockCount++;
                    string type = block.Definition.DebugNameShort;
                    blockTypes[type] = blockTypes.GetValueOrDefault(type) + 1;
                    var functional = block.Entity.TryGet<FunctionalBlockComponent>();
                    if (functional == null) continue;
                    var cell = block.GetFirstOccupiedCell();
                    devices.Add(new DeviceInfo(block.DEntity.ToString(), functional.DisplayName.EvaluateText(),
                        type, block.Definition.Guid.ToString(), cell.ToString(), functional.Functional,
                        functional.Working, block.BuildProgress,
                        block.Entity.Components.Select(c => c.GetType().Name).Order(StringComparer.Ordinal).ToArray()));
                }
                grids.Add(new GridInfo(entity.DEntity.ToString(), grid.DisplayName.EvaluateText(),
                    grid.GridIsTurnedOn, blockCount, devices.Count,
                    blockTypes.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
                    devices.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray()));
            }
            grids.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            watch.Stop();
            lock (gate)
            {
                if (disposed || scanGeneration != generation) return;
                var snapshot = new { schemaVersion = 1, runId, sessionGeneration = generation,
                    processId = Environment.ProcessId, utc = DateTimeOffset.UtcNow, state = "READY",
                    scope = "loaded-server-scene", idScope = "runtime-session-only",
                    scanMilliseconds = watch.Elapsed.TotalMilliseconds, gridCount = grids.Count,
                    deviceCount = grids.Sum(g => g.DeviceCount), grids };
                Save(JsonSerializer.Serialize(snapshot, Json));
                var currentIds = grids.Select(g => g.Id).ToHashSet();
                foreach (var id in previous.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                {
                    Emit("GRID_REMOVED", new { id });
                    previous.Remove(id);
                }
                foreach (var grid in grids)
                {
                    string serialized = JsonSerializer.Serialize(grid, Json);
                    if (previous.GetValueOrDefault(grid.Id) == serialized) continue;
                    Emit("GRID", new { grid.Id, grid.Name, grid.Powered, grid.BlockCount, grid.DeviceCount, grid.BlockTypes });
                    foreach (var device in grid.Devices) Emit("DEVICE", new { gridId = grid.Id, device });
                    previous[grid.Id] = serialized;
                }
                if (lastState != "READY") Emit("GRID_SCAN_READY", new { snapshot.gridCount, snapshot.deviceCount, snapshot.scope });
                lastState = "READY";
            }
        }
        catch (Exception e)
        {
            lock (gate)
                if (!disposed && scanGeneration == generation) PublishState("SCAN_ERROR", e.ToString());
        }
        finally
        {
            lock (gate) if (scanGeneration == generation) pending = false;
        }
    }

    private void PublishState(string state, string? error = null)
    {
        Save(JsonSerializer.Serialize(new { schemaVersion = 1, runId, sessionGeneration = generation,
            processId = Environment.ProcessId, utc = DateTimeOffset.UtcNow, state, error, grids = Array.Empty<GridInfo>() }, Json));
        string key = state + error;
        if (lastState != key) Emit(state, new { error });
        lastState = key;
    }

    private void Save(string json)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "grids.json");
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
    }

    private void Emit(string kind, object data)
    {
        string line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, runId,
            sessionGeneration = generation, kind, data }, Json);
        Keen.VRage.Library.Diagnostics.Log.Default.WriteLine($"[SE2GameAPI] {line}");
        File.AppendAllText(Path.Combine(directory, $"grids-{runId}.jsonl"), line + Environment.NewLine);
    }

    public void Dispose() { lock (gate) { disposed = true; generation++; activeSession = null; } }

    internal sealed record GridInfo(string Id, string Name, bool Powered, int BlockCount, int DeviceCount,
        Dictionary<string, int> BlockTypes, DeviceInfo[] Devices);
    internal sealed record DeviceInfo(string Id, string Name, string Type, string DefinitionId, string Cell,
        bool Functional, bool Working, float BuildProgress, string[] Components);
}
