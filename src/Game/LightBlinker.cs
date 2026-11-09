using System.Diagnostics;
using System.Text.Json;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Utils;

namespace SE2GameAPI.Game;

internal sealed class LightBlinker(string directory, string runId)
{
    internal sealed record Settings(bool Enabled = false, string GridName = "Concordia Research Station",
        string LampName = "Interior Light 0.5 m", int IntervalMs = 1000, int ExpectedCount = 3);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private Session? session;
    private int generation;
    private bool pending;
    private Settings? applied;
    private long nextToggle;
    private bool phase;
    private string? lastLog;
    private readonly Dictionary<PowerableBlockComponent, bool> original = new();

    public void Poll()
    {
        var app = Singleton<VRageCore>.Instance.Engine.Components.FirstOrDefault(c => c.GetType().FullName == "Keen.Game2.GameAppComponent");
        var current = app?.GetType().GetProperty("ServerSession")?.GetValue(app) as Session;
        lock (gate)
        {
            if (!ReferenceEquals(current, session))
            {
                session = current; generation++; pending = false; original.Clear(); applied = null; nextToggle = 0;
            }
            if (current == null || current.IsUnloading) return;
            if (pending) return;
            pending = true;
            _ = TickAsync(current, generation);
        }
    }

    private async Task TickAsync(Session current, int token)
    {
        try
        {
            Settings config;
            string? configError = null;
            try
            {
                string path = Path.Combine(directory, "blink.json");
                config = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new();
                if (config.IntervalMs < 500 || config.IntervalMs > 60000 || config.ExpectedCount < 1 || config.ExpectedCount > 100 ||
                    string.IsNullOrWhiteSpace(config.GridName) || string.IsNullOrWhiteSpace(config.LampName))
                    throw new InvalidDataException("Specify grid/lamp names, expectedCount 1..100 and intervalMs 500..60000.");
            }
            catch (Exception e) { config = new(); configError = e.Message; }

            await current.Scene.MoveToDCS();
            lock (gate)
            {
                if (token != generation || current.IsUnloading) return;
                // Re-resolve live objects every tick; never operate on a removed entity.
                var grids = current.GetEntitiesOfType<CubeGridComponent>().Select(e => e.Get<CubeGridComponent>()).Where(g => !g.GridClosing).ToArray();
                var live = new HashSet<PowerableBlockComponent>();
                foreach (var grid in grids)
                    foreach (var block in grid.CubeBlockStorage.GetAllCubeBlocks())
                        if (block != null && !block.IsDestroyed && block.Entity.TryGet<PowerableBlockComponent>() is { } p) live.Add(p);

                if (applied != config || !config.Enabled)
                {
                    Restore(live);
                    applied = config;
                    nextToggle = 0;
                }
                if (!config.Enabled) { Report(configError == null ? "STOPPED" : "CONFIG_ERROR", configError, []); return; }
                var targets = grids.Where(g => g.DisplayName.EvaluateText() == config.GridName).ToArray();
                var lamps = new List<PowerableBlockComponent>();
                if (targets.Length == 1)
                    foreach (var block in targets[0].CubeBlockStorage.GetAllCubeBlocks())
                        if (block != null && !block.IsDestroyed &&
                            block.Definition.DebugNameShort == "InteriorLight50_PointLightPowerableBlockDefinition" &&
                            block.Entity.TryGet<PowerableBlockComponent>() is { } light &&
                            light.DisplayName.EvaluateText().Replace('\u00a0', ' ') == config.LampName.Replace('\u00a0', ' ')) lamps.Add(light);
                if (targets.Length != 1 || lamps.Count != config.ExpectedCount)
                {
                    Restore(live); nextToggle = 0;
                    Report("TARGET_MISMATCH", $"Matched grids={targets.Length}, lamps={lamps.Count}; expected one grid and {config.ExpectedCount} lamps.", []);
                    return;
                }
                if (original.Keys.Any(p => !lamps.Contains(p))) { Restore(live); nextToggle = 0; }
                foreach (var lamp in lamps) original.TryAdd(lamp, lamp.Enabled);
                long now = Stopwatch.GetTimestamp();
                if (now >= nextToggle)
                {
                    phase = nextToggle == 0 || !phase;
                    foreach (var lamp in lamps) lamp.Enabled = phase;
                    nextToggle = now + (long)(config.IntervalMs / 1000.0 * Stopwatch.Frequency);
                }
                Report(lamps.All(p => p.Enabled == phase) ? "BLINKING" : "STATE_MISMATCH", null,
                    lamps.Select(p => new LampState(p.DEntity.ToString(), p.DisplayName.EvaluateText(), p.Enabled, p.Working, original[p])).ToArray());
            }
        }
        catch (Exception e)
        {
            try { Report("ERROR", e.ToString(), []); } catch { }
        }
        finally { lock (gate) if (token == generation) pending = false; }
    }

    private void Restore(HashSet<PowerableBlockComponent> live)
    {
        foreach (var (lamp, enabled) in original)
            if (live.Contains(lamp)) lamp.Enabled = enabled;
        if (original.Count > 0)
            Keen.VRage.Library.Diagnostics.Log.Default.WriteLine($"[SE2GameAPI] BLINK_RESTORED {original.Count} tracked lamps (removed blocks skipped)");
        original.Clear();
    }

    private void Report(string state, string? error, LampState[] lamps)
    {
        var data = new { runId, sessionGeneration = generation, utc = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId, state, requestedEnabled = phase, error, lamps };
        string json = JsonSerializer.Serialize(data, Json);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "blink-status.json");
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
        string key = JsonSerializer.Serialize(new { state, error, lamps }, Json);
        if (key == lastLog) return;
        lastLog = key;
        Keen.VRage.Library.Diagnostics.Log.Default.WriteLine($"[SE2GameAPI] BLINK {json}");
        File.AppendAllText(Path.Combine(directory, $"blink-{runId}.jsonl"), json + Environment.NewLine);
    }

    private sealed record LampState(string Id, string Name, bool Enabled, bool Working, bool OriginalEnabled);
}
