namespace ControlRoom.Domain.Model;

/// <summary>
/// Rich summary of a run's execution, stored as JSON in the summary column.
/// </summary>
public sealed record RunSummary(
    RunStatus Status,
    TimeSpan Duration,
    int StdOutLines,
    int StdErrLines,
    int? ExitCode,
    string? FailureFingerprint,  // SHA256 of stderr for grouping identical failures
    string? LastStdErrLine,      // Quick preview of what went wrong
    int ArtifactCount,
    string? CommandLine = null,  // Resolved command line for reproducibility
    string? WorkingDirectory = null,  // Working directory used
    string? RunDirectory = null,  // Path to run's artifact/evidence directory
    string? ProfileId = null,  // Profile used for this run
    string? ProfileName = null,  // Profile display name
    string? ArgsResolved = null,  // Final args passed to script
    Dictionary<string, string>? EnvOverrides = null  // Env vars added by profile
)
{
    public string ToDisplayString()
    {
        var parts = new List<string> { Status.ToString() };

        if (Duration.TotalSeconds >= 1)
            parts.Add($"{Duration.TotalSeconds:F1}s");
        else
            parts.Add($"{Duration.TotalMilliseconds:F0}ms");

        if (StdOutLines > 0)
            parts.Add($"{StdOutLines} lines");

        if (StdErrLines > 0)
            parts.Add($"{StdErrLines} errors");

        if (ArtifactCount > 0)
            parts.Add($"{ArtifactCount} artifacts");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// One-line pasteable summary for sharing
    /// </summary>
    public string ToCopyableString(string thingName, DateTimeOffset startedAt)
    {
        var statusIcon = Status switch
        {
            RunStatus.Succeeded => "✓",
            RunStatus.Failed => "✗",
            RunStatus.Canceled => "⊘",
            RunStatus.Running => "⋯",
            _ => "?"
        };

        var duration = Duration.TotalSeconds >= 1
            ? $"{Duration.TotalSeconds:F1}s"
            : $"{Duration.TotalMilliseconds:F0}ms";

        var exit = ExitCode.HasValue ? $"exit {ExitCode}" : "";

        return $"{statusIcon} {thingName} @ {startedAt:yyyy-MM-dd HH:mm} · {duration} · {StdOutLines} out · {StdErrLines} err {exit}".Trim();
    }
}
