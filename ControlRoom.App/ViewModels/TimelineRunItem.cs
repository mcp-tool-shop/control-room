using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

/// <summary>
/// ViewModel wrapper for timeline items with computed display properties
/// </summary>
public sealed class TimelineRunItem
{
    private readonly RunListItem _run;
    private readonly RunSummary? _summary;
    private readonly int _recurrenceCount;

    public TimelineRunItem(RunListItem run, int recurrenceCount = 0)
    {
        _run = run;
        _summary = run.GetParsedSummary();
        _recurrenceCount = recurrenceCount;
    }

    public RunId RunId => _run.RunId;
    public ThingId ThingId => _run.ThingId;
    public string ThingName => _run.ThingName;
    public DateTimeOffset StartedAt => _run.StartedAt;
    public RunStatus Status => _run.Status;
    public int? ExitCode => _run.ExitCode;

    /// <summary>
    /// Duration as a readable string (e.g., "1.2s" or "450ms")
    /// </summary>
    public string DurationText
    {
        get
        {
            if (_summary is null) return "";

            return _summary.Duration.TotalSeconds >= 1
                ? $"{_summary.Duration.TotalSeconds:F1}s"
                : $"{_summary.Duration.TotalMilliseconds:F0}ms";
        }
    }

    /// <summary>
    /// Output line counts (e.g., "43 out · 0 err")
    /// </summary>
    public string OutputText
    {
        get
        {
            if (_summary is null) return "";

            var parts = new List<string>();
            if (_summary.StdOutLines > 0)
                parts.Add($"{_summary.StdOutLines} out");
            if (_summary.StdErrLines > 0)
                parts.Add($"{_summary.StdErrLines} err");
            if (_summary.ArtifactCount > 0)
                parts.Add($"{_summary.ArtifactCount} files");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// For failed runs, shows recurrence count (e.g., "Recurring ×3")
    /// </summary>
    public string RecurrenceText
    {
        get
        {
            if (_run.Status != RunStatus.Failed || _recurrenceCount <= 1)
                return "";
            return $"×{_recurrenceCount}";
        }
    }

    /// <summary>
    /// Whether this is a recurring failure
    /// </summary>
    public bool IsRecurring => _recurrenceCount > 1;

    /// <summary>
    /// Whether we have summary data to display
    /// </summary>
    public bool HasSummary => _summary is not null;
}
