namespace SquadHostManager.Core;

// Estimate only this download stage, never the whole provisioning process.
public sealed class DownloadEstimate
{
    private string? stage;
    private readonly Queue<(DateTimeOffset Time, double Percent)> samples = new();
    public TimeSpan? Observe(string currentStage, double? percent, DateTimeOffset now)
    {
        if (stage != currentStage || percent == null || (samples.Count > 0 && percent < samples.Last().Percent))
        { samples.Clear(); stage = currentStage; }
        if (percent is not (> 0 and < 100)) return null;
        samples.Enqueue((now, percent.Value));
        while (samples.Count > 2 && now - samples.Peek().Time > TimeSpan.FromMinutes(2)) samples.Dequeue();
        var first = samples.Peek(); var seconds = (now - first.Time).TotalSeconds;
        var gain = percent.Value - first.Percent;
        if (seconds < 20 || gain < 0.1 || now - samples.Last(s => s.Percent < percent.Value || s == first).Time > TimeSpan.FromSeconds(60)) return null;
        var remaining = (100 - percent.Value) * seconds / gain;
        return remaining <= 86400 ? TimeSpan.FromSeconds(remaining) : null;
    }
}
