using Keen.VRage.Core.Game.Systems;
using SE2GameAPI.Game.Devices;
using SE2GameAPI.Redis;
namespace SE2GameAPI.Game;

// One round-robin lane per target. A moving door blocks only its own following commands.
internal sealed class CommandRouter
{
    private sealed class Lane
    {
        public readonly Queue<Work> Waiting = new();
        public Work? Active;
        public DeviceAdapter? Device;
        public Func<DeviceState, bool>? Confirm;
    }
    private readonly Dictionary<string, Lane> lanes = new();
    private readonly Queue<string> ready = new();
    public int Depth { get; private set; }
    public long Applied { get; private set; }
    private static void Finish(Work work, string status, string? error, DeviceState? state = null)
        => work.Completion.TrySetResult(new(work.Command.RequestId, status, error, state));
    public void Reset()
    {
        foreach (var lane in lanes.Values)
        {
            if (lane.Active != null) Finish(lane.Active, "rejected", "stale_session");
            foreach (var work in lane.Waiting) Finish(work, "rejected", "stale_session");
        }
        lanes.Clear(); ready.Clear(); Depth = 0;
    }
    public void Run(Session session, string epoch, DeviceRegistry registry, RedisGateway gateway, SceneBudget budget)
    {
        int admitted = 0;
        while (Depth < 64 && admitted++ < 16 && budget.HasTime && gateway.TryRead(out var work))
        {
            string id = work!.Command.DeviceId ?? "";
            if (!lanes.TryGetValue(id, out var lane)) { lanes.Add(id, lane = new()); ready.Enqueue(id); }
            if (lane.Waiting.Count >= 8) { Finish(work, "rejected", "target_busy"); continue; }
            lane.Waiting.Enqueue(work); Depth++;
        }
        int turns = Math.Min(ready.Count, 16);
        while (turns-- > 0 && budget.HasTime)
        {
            string id = ready.Dequeue(); var lane = lanes[id];
            try
            {
                if (lane.Active != null)
                {
                    var active = lane.Active;
                    var actual = lane.Device!.Read(session);
                    bool confirmed = actual != null && lane.Confirm!(actual);
                    if (active.Completion.Task.IsCompleted || confirmed || actual == null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= active.Command.ExpiresAtMs)
                    {
                        Finish(active, confirmed ? "confirmed" : "failed", confirmed ? null : actual == null ? "device_removed" : "state_timeout", actual);
                        lane.Active = null; lane.Device = null; lane.Confirm = null; Depth--;
                    }
                }
                else if (lane.Waiting.TryDequeue(out var work))
                {
                    Depth--;
                    if (work.Completion.Task.IsCompleted) continue;
                    var cmd = work.Command;
                    if (cmd.Epoch != epoch) { Finish(work, "rejected", "stale_session"); continue; }
                    if (cmd.ExpiresAtMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { Finish(work, "rejected", "expired"); continue; }
                    if (!registry.Devices.TryGetValue(id, out var device)) { Finish(work, "rejected", "not_found"); continue; }
                    var before = device.Read(session);
                    if (before == null || before.GridId != cmd.GridId) { Finish(work, "rejected", "target_changed"); continue; }
                    if (cmd.ExpectedRevision.HasValue && cmd.ExpectedRevision != before.Revision) { Finish(work, "rejected", "revision_conflict", before); continue; }
                    try
                    {
                        var confirm = device.Apply(cmd, before);
                        if (confirm == null) { Finish(work, "rejected", "unsupported_command", before); continue; }
                        lane.Active = work; lane.Device = device; lane.Confirm = confirm; Depth++; Applied++;
                    }
                    catch (Exception e) { Finish(work, "failed", e.Message, before); }
                }
            }
            catch (Exception e)
            {
                if (lane.Active != null) { Finish(lane.Active, "failed", e.Message); lane.Active = null; Depth--; }
            }
            finally
            {
                if (lane.Active != null || lane.Waiting.Count > 0) ready.Enqueue(id);
                else lanes.Remove(id);
            }
        }
    }
}
