using System.Diagnostics;
namespace SE2GameAPI.Game;
internal readonly struct SceneBudget
{
    private readonly long started = Stopwatch.GetTimestamp();
    public SceneBudget() { }
    public double ElapsedMs => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    public bool HasTime => ElapsedMs < .5;
}
