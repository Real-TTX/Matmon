namespace Matmon.Core.Domain;

/// <summary>
/// What a status text read from somebody else's page means: "Operational" / "Degraded Performance" / "Major Outage".
/// Status pages (status.io, Atlassian Statuspage, Better Uptime, instatus ...) and health endpoints all say it in words,
/// and they agree on the words closely enough for one list. A text none of the words covers is no status (null) - the
/// HTTP Advanced sensor then skips it rather than guessing.
/// </summary>
public static class StatusText
{
    // Checked in this order: "partial outage" is a partial problem, not an outage; "no issues" is fine, not an issue.
    private static readonly string[] Healthy =
    [
        "operational", "all systems operational", "no issues", "not affected", "healthy", "online", "available", "normal",
        "ok", "up", "pass", "passing", "green", "good", "running", "resolved", "completed", "none"
    ];

    private static readonly string[] Warning =
    [
        "partial", "degraded", "maintenance", "minor", "warning", "warn", "slow", "elevated", "limited", "investigating",
        "identified", "monitoring", "under_maintenance", "yellow", "amber", "delayed", "impaired", "informational", "notice"
    ];

    private static readonly string[] Critical =
    [
        "major", "outage", "disruption", "down", "offline", "unavailable", "critical", "error", "failed", "failing",
        "fail", "red", "security", "unhealthy"
    ];

    /// <summary>Healthy / Warning / Critical for a known status text, else null.</summary>
    public static SensorState? Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 120)
        {
            return null;
        }

        var normalized = text.Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (Healthy.Any(phrase => phrase.Contains(' ') ? normalized == phrase || normalized.StartsWith(phrase + " ", StringComparison.Ordinal) : false))
        {
            return SensorState.Healthy;
        }

        if (words.Any(word => Warning.Contains(word)) || normalized.Contains("under maintenance", StringComparison.Ordinal))
        {
            return SensorState.Warning;
        }

        if (words.Any(word => Critical.Contains(word)))
        {
            return SensorState.Critical;
        }

        return words.Length <= 3 && words.Any(word => Healthy.Contains(word)) ? SensorState.Healthy : null;
    }
}
