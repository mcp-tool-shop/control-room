using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

/// <summary>
/// ViewModel wrapper for failure groups with computed display properties
/// </summary>
public sealed class FailureGroupItem
{
    private readonly FailureGroup _group;

    public FailureGroupItem(FailureGroup group)
    {
        _group = group;
    }

    public string Fingerprint => _group.Fingerprint;
    public int Count => _group.Count;
    public DateTimeOffset FirstSeen => _group.FirstSeen;
    public DateTimeOffset LastSeen => _group.LastSeen;
    public RunId LatestRunId => _group.LatestRunId;
    public RunId FirstRunId => _group.FirstRunId;
    public string ThingName => _group.ThingName;
    public string? LastStdErrLine => _group.LastStdErrLine;
    public int DistinctThingCount => _group.DistinctThingCount;

    /// <summary>
    /// Count badge text (e.g., "×3")
    /// </summary>
    public string CountBadge => $"×{Count}";

    /// <summary>
    /// Whether this is a recurring failure (count > 1)
    /// </summary>
    public bool IsRecurring => Count > 1;

    /// <summary>
    /// Preview of the error (truncated if needed)
    /// </summary>
    public string PreviewText
    {
        get
        {
            if (string.IsNullOrEmpty(LastStdErrLine))
                return "Unknown error";

            return LastStdErrLine.Length > 80
                ? LastStdErrLine[..77] + "..."
                : LastStdErrLine;
        }
    }

    /// <summary>
    /// "Seen in X Things" or "In: ThingName"
    /// </summary>
    public string ThingsText
    {
        get
        {
            if (DistinctThingCount > 1)
                return $"Seen in {DistinctThingCount} Things";
            return $"In: {ThingName}";
        }
    }

    /// <summary>
    /// Relative time since last occurrence
    /// </summary>
    public string LastSeenText
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - LastSeen;

            if (elapsed.TotalMinutes < 1)
                return "Just now";
            if (elapsed.TotalMinutes < 60)
                return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24)
                return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 7)
                return $"{(int)elapsed.TotalDays}d ago";

            return LastSeen.LocalDateTime.ToString("MMM d");
        }
    }

    /// <summary>
    /// Short fingerprint for display (first 8 chars)
    /// </summary>
    public string ShortFingerprint => Fingerprint.Length > 8 ? Fingerprint[..8] : Fingerprint;
}
