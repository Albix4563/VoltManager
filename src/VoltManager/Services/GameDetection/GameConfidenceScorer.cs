namespace VoltManager.Services.GameDetection;

public static class GameConfidenceScorer
{
    private static readonly IReadOnlyDictionary<string, int> GroupCaps =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["identity"] = 50,
            ["provenance"] = 35,
            ["runtime"] = 25,
            ["history"] = 25,
        };

    public static GameDetectionAssessment Score(
        IEnumerable<GameDetectionEvidence> evidence,
        string? primaryReason = null)
    {
        var items = evidence.ToArray();
        long positives = items
            .Where(item => item.Weight > 0 && GroupCaps.ContainsKey(item.Group))
            .GroupBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .Sum(group => Math.Min((long)GroupCaps[group.Key], group.Sum(item => (long)item.Weight)));
        long penalties = items.Where(item => item.Weight < 0).Sum(item => (long)item.Weight);
        int score = (int)Math.Clamp(positives + penalties, 0L, 100L);

        return new GameDetectionAssessment
        {
            PrimaryReason = primaryReason,
            Score = score,
            Level = LevelFor(score),
            Evidence = items,
        };
    }

    public static string LevelFor(int score) => Math.Clamp(score, 0, 100) switch
    {
        >= 75 => "confirmed",
        >= 60 => "probable",
        >= 40 => "unknown",
        _ => "ignored",
    };
}
