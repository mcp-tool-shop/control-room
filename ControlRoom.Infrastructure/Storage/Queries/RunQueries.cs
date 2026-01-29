using System.Text.Json;
using ControlRoom.Domain.Model;

namespace ControlRoom.Infrastructure.Storage.Queries;

public sealed record RunListItem(
    RunId RunId,
    ThingId ThingId,
    string ThingName,
    DateTimeOffset StartedAt,
    RunStatus Status,
    int? ExitCode,
    string? Summary
)
{
    /// <summary>
    /// Parse the Summary JSON to get the RunSummary record.
    /// Returns null if Summary is null or invalid JSON.
    /// </summary>
    public RunSummary? GetParsedSummary()
    {
        if (string.IsNullOrEmpty(Summary)) return null;
        try
        {
            return JsonSerializer.Deserialize<RunSummary>(Summary);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record RunEventItem(
    long Seq,
    DateTimeOffset At,
    EventKind Kind,
    string PayloadJson
);

/// <summary>
/// Represents a group of failures with the same fingerprint
/// </summary>
public sealed record FailureGroup(
    string Fingerprint,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    RunId LatestRunId,
    RunId FirstRunId,
    string ThingName,
    string? LastStdErrLine,
    int DistinctThingCount
);

public sealed class RunQueries
{
    private readonly Db _db;
    public RunQueries(Db db) => _db = db;

    public IReadOnlyList<RunListItem> ListRuns(int limit = 200)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_id, r.thing_id, t.name, r.started_at, r.status, r.exit_code, r.summary
            FROM runs r
            JOIN things t ON t.thing_id = r.thing_id
            ORDER BY r.started_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<RunListItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RunListItem(
                new RunId(Guid.Parse(reader.GetString(0))),
                new ThingId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                (RunStatus)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)
            ));
        }
        return list;
    }

    public IReadOnlyList<RunEventItem> ListRunEvents(RunId runId, long afterSeq = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT seq, at, kind, payload_json
            FROM run_events
            WHERE run_id = $run_id AND seq > $after
            ORDER BY seq ASC
            """;
        cmd.Parameters.AddWithValue("$run_id", runId.ToString());
        cmd.Parameters.AddWithValue("$after", afterSeq);

        var list = new List<RunEventItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RunEventItem(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                (EventKind)reader.GetInt32(2),
                reader.GetString(3)
            ));
        }
        return list;
    }

    /// <summary>
    /// Get the last successful run for a specific Thing
    /// </summary>
    public RunListItem? GetLastSuccess(ThingId thingId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_id, r.thing_id, t.name, r.started_at, r.status, r.exit_code, r.summary
            FROM runs r
            JOIN things t ON t.thing_id = r.thing_id
            WHERE r.thing_id = $thing_id AND r.status = $status
            ORDER BY r.started_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$thing_id", thingId.ToString());
        cmd.Parameters.AddWithValue("$status", (int)RunStatus.Succeeded);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new RunListItem(
                new RunId(Guid.Parse(reader.GetString(0))),
                new ThingId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                (RunStatus)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)
            );
        }
        return null;
    }

    /// <summary>
    /// Get recurring failures grouped by fingerprint, ordered by count (most common first)
    /// </summary>
    public IReadOnlyList<FailureGroup> GetRecurringFailures(int limit = 20)
    {
        return GetFailureGroups(minCount: 2, limit);
    }

    /// <summary>
    /// Get all failures grouped by fingerprint (including single occurrences), ordered by count then last_seen
    /// </summary>
    public IReadOnlyList<FailureGroup> GetAllFailureGroups(int limit = 100)
    {
        return GetFailureGroups(minCount: 1, limit);
    }

    private IReadOnlyList<FailureGroup> GetFailureGroups(int minCount, int limit)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();

        // Extract fingerprint from JSON summary using SQLite json_extract
        cmd.CommandText = """
            WITH failures AS (
                SELECT
                    r.run_id,
                    r.thing_id,
                    t.name as thing_name,
                    r.started_at,
                    r.summary,
                    json_extract(r.summary, '$.FailureFingerprint') as fingerprint,
                    json_extract(r.summary, '$.LastStdErrLine') as last_err
                FROM runs r
                JOIN things t ON t.thing_id = r.thing_id
                WHERE r.status = $failed_status
                  AND r.summary IS NOT NULL
                  AND json_extract(r.summary, '$.FailureFingerprint') IS NOT NULL
            )
            SELECT
                fingerprint,
                COUNT(*) as cnt,
                MIN(started_at) as first_seen,
                MAX(started_at) as last_seen,
                (SELECT run_id FROM failures f2
                 WHERE f2.fingerprint = failures.fingerprint
                 ORDER BY started_at DESC LIMIT 1) as latest_run_id,
                (SELECT run_id FROM failures f2
                 WHERE f2.fingerprint = failures.fingerprint
                 ORDER BY started_at ASC LIMIT 1) as first_run_id,
                (SELECT thing_name FROM failures f2
                 WHERE f2.fingerprint = failures.fingerprint
                 ORDER BY started_at DESC LIMIT 1) as thing_name,
                (SELECT last_err FROM failures f2
                 WHERE f2.fingerprint = failures.fingerprint
                 ORDER BY started_at DESC LIMIT 1) as last_err,
                COUNT(DISTINCT thing_id) as distinct_things
            FROM failures
            GROUP BY fingerprint
            HAVING COUNT(*) >= $min_count
            ORDER BY cnt DESC, last_seen DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$failed_status", (int)RunStatus.Failed);
        cmd.Parameters.AddWithValue("$min_count", minCount);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<FailureGroup>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FailureGroup(
                reader.GetString(0),
                reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3)),
                new RunId(Guid.Parse(reader.GetString(4))),
                new RunId(Guid.Parse(reader.GetString(5))),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8)
            ));
        }
        return list;
    }

    /// <summary>
    /// Get the count of runs with the same fingerprint as a given run
    /// </summary>
    public int GetRecurrenceCount(string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return 0;

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM runs
            WHERE status = $failed_status
              AND summary IS NOT NULL
              AND json_extract(summary, '$.FailureFingerprint') = $fp
            """;
        cmd.Parameters.AddWithValue("$failed_status", (int)RunStatus.Failed);
        cmd.Parameters.AddWithValue("$fp", fingerprint);

        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Get the most common failure (by fingerprint) across all Things
    /// </summary>
    public FailureGroup? GetMostCommonFailure()
    {
        var failures = GetRecurringFailures(1);
        return failures.Count > 0 ? failures[0] : null;
    }

    /// <summary>
    /// Get all runs with a specific failure fingerprint, ordered by most recent first
    /// </summary>
    public IReadOnlyList<RunListItem> ListRunsByFingerprint(string fingerprint, int limit = 50)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_id, r.thing_id, t.name, r.started_at, r.status, r.exit_code, r.summary
            FROM runs r
            JOIN things t ON t.thing_id = r.thing_id
            WHERE r.status = $failed_status
              AND r.summary IS NOT NULL
              AND json_extract(r.summary, '$.FailureFingerprint') = $fp
            ORDER BY r.started_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$failed_status", (int)RunStatus.Failed);
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<RunListItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RunListItem(
                new RunId(Guid.Parse(reader.GetString(0))),
                new ThingId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                (RunStatus)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)
            ));
        }
        return list;
    }

    /// <summary>
    /// Get the first (oldest) run with a specific failure fingerprint
    /// </summary>
    public RunListItem? GetFirstRunForFingerprint(string fingerprint)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_id, r.thing_id, t.name, r.started_at, r.status, r.exit_code, r.summary
            FROM runs r
            JOIN things t ON t.thing_id = r.thing_id
            WHERE r.status = $failed_status
              AND r.summary IS NOT NULL
              AND json_extract(r.summary, '$.FailureFingerprint') = $fp
            ORDER BY r.started_at ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$failed_status", (int)RunStatus.Failed);
        cmd.Parameters.AddWithValue("$fp", fingerprint);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new RunListItem(
                new RunId(Guid.Parse(reader.GetString(0))),
                new ThingId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                (RunStatus)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)
            );
        }
        return null;
    }

    /// <summary>
    /// Get the ThingId for the most recent run with a specific failure fingerprint
    /// </summary>
    public ThingId? GetLatestThingForFingerprint(string fingerprint)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.thing_id
            FROM runs r
            WHERE r.status = $failed_status
              AND r.summary IS NOT NULL
              AND json_extract(r.summary, '$.FailureFingerprint') = $fp
            ORDER BY r.started_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$failed_status", (int)RunStatus.Failed);
        cmd.Parameters.AddWithValue("$fp", fingerprint);

        var result = cmd.ExecuteScalar();
        if (result is string thingIdStr)
        {
            return new ThingId(Guid.Parse(thingIdStr));
        }
        return null;
    }
}
