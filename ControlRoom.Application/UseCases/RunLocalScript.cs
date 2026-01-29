using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Process;
using ControlRoom.Infrastructure.Storage;

namespace ControlRoom.Application.UseCases;

public sealed class RunLocalScript
{
    private readonly Db _db;
    private readonly IScriptRunner _runner;

    public RunLocalScript(Db db, IScriptRunner runner)
    {
        _db = db;
        _runner = runner;
    }

    /// <summary>
    /// Execute a Thing with the default profile
    /// </summary>
    public Task<RunId> ExecuteAsync(Thing thing, string args, CancellationToken ct)
    {
        return ExecuteWithProfileAsync(thing, profileId: null, argsOverride: args, ct);
    }

    /// <summary>
    /// Execute a Thing with a specific profile
    /// </summary>
    public async Task<RunId> ExecuteWithProfileAsync(Thing thing, string? profileId, string? argsOverride, CancellationToken ct)
    {
        var runId = RunId.New();
        var startedAt = DateTimeOffset.UtcNow;

        // Parse config and resolve profile
        var config = ThingConfig.Parse(thing.ConfigJson);
        var profile = string.IsNullOrEmpty(profileId)
            ? config.GetDefaultProfile()
            : config.GetProfile(profileId) ?? config.GetDefaultProfile();

        // Resolve args: override > profile args > empty
        var resolvedArgs = !string.IsNullOrEmpty(argsOverride)
            ? argsOverride
            : profile.Args;

        // Tracking for intelligent summary
        int stdOutLines = 0;
        int stdErrLines = 0;
        var stderrBuilder = new StringBuilder();
        string? lastStdErrLine = null;

        using (var conn = _db.Open())
        using (var tx = conn.BeginTransaction())
        {
            Exec(conn, tx, """
                INSERT INTO runs(run_id, thing_id, started_at, status)
                VALUES ($run_id, $thing_id, $started_at, $status)
                """,
                ("$run_id", runId.ToString()),
                ("$thing_id", thing.Id.ToString()),
                ("$started_at", startedAt.ToString("O")),
                ("$status", (int)RunStatus.Running));

            AddEvent(conn, tx, runId, startedAt, EventKind.RunStarted, new
            {
                thingName = thing.Name,
                profileId = profile.Id,
                profileName = profile.Name
            });

            tx.Commit();
        }

        var spec = BuildSpecFromConfig(config, profile, resolvedArgs);

        // Set up run directory for this run (evidence package)
        var runDir = GetRunDirectory(runId);
        Directory.CreateDirectory(runDir);

        // Build environment: Control Room vars + profile env overrides
        var env = new Dictionary<string, string>
        {
            ["CONTROLROOM_RUN_DIR"] = runDir,
            ["CONTROLROOM_ARTIFACT_DIR"] = runDir,  // Legacy compat
            ["CONTROLROOM_RUN_ID"] = runId.ToString(),
            ["CONTROLROOM_PROFILE_ID"] = profile.Id,
            ["CONTROLROOM_PROFILE_NAME"] = profile.Name
        };

        // Add profile environment overrides
        foreach (var (key, value) in profile.Env)
        {
            env[key] = value;
        }

        var specWithEnv = spec with { Env = env };

        var result = await _runner.RunAsync(
            specWithEnv,
            async (isErr, line) =>
            {
                // Track line counts and stderr content
                if (isErr)
                {
                    Interlocked.Increment(ref stdErrLines);
                    lock (stderrBuilder)
                    {
                        stderrBuilder.AppendLine(line);
                        lastStdErrLine = line;
                    }
                }
                else
                {
                    Interlocked.Increment(ref stdOutLines);
                }

                using var conn = _db.Open();
                using var tx = conn.BeginTransaction();

                var kind = isErr ? EventKind.StdErr : EventKind.StdOut;
                AddEvent(conn, tx, runId, DateTimeOffset.UtcNow, kind, new { line });

                tx.Commit();
                await Task.CompletedTask;
            },
            ct);

        var endedAt = DateTimeOffset.UtcNow;
        var duration = endedAt - startedAt;

        var finalStatus = result.WasCanceled
            ? RunStatus.Canceled
            : (result.ExitCode == 0 ? RunStatus.Succeeded : RunStatus.Failed);

        // Capture artifacts created during the run
        var artifacts = await CaptureArtifactsAsync(runId, runDir);

        // Build failure fingerprint from stderr + exit code (for grouping identical failures)
        string? failureFingerprint = null;
        if (finalStatus == RunStatus.Failed)
        {
            var stderrContent = stderrBuilder.ToString();
            failureFingerprint = ComputeFailureFingerprint(result.ExitCode, stderrContent);
        }

        // Build rich summary with reproducibility info
        var summary = new RunSummary(
            Status: finalStatus,
            Duration: duration,
            StdOutLines: stdOutLines,
            StdErrLines: stdErrLines,
            ExitCode: result.ExitCode,
            FailureFingerprint: failureFingerprint,
            LastStdErrLine: lastStdErrLine?.Length > 200 ? lastStdErrLine[..200] : lastStdErrLine,
            ArtifactCount: artifacts.Count,
            CommandLine: result.ResolvedCommandLine,
            WorkingDirectory: spec.WorkingDirectory ?? Path.GetDirectoryName(spec.FilePath),
            RunDirectory: runDir,
            ProfileId: profile.Id,
            ProfileName: profile.Name,
            ArgsResolved: resolvedArgs,
            EnvOverrides: profile.Env.Count > 0 ? profile.Env : null
        );

        var summaryJson = JsonSerializer.Serialize(summary);

        using (var conn = _db.Open())
        using (var tx = conn.BeginTransaction())
        {
            Exec(conn, tx, """
                UPDATE runs
                SET ended_at = $ended_at,
                    status   = $status,
                    exit_code= $exit_code,
                    summary  = $summary
                WHERE run_id = $run_id
                """,
                ("$ended_at", endedAt.ToString("O")),
                ("$status", (int)finalStatus),
                ("$exit_code", (object?)result.ExitCode ?? DBNull.Value),
                ("$summary", summaryJson),
                ("$run_id", runId.ToString()));

            // Insert artifacts
            foreach (var artifact in artifacts)
            {
                Exec(conn, tx, """
                    INSERT INTO artifacts(artifact_id, run_id, media_type, locator, sha256_hex, created_at)
                    VALUES ($id, $run_id, $media, $locator, $hash, $at)
                    """,
                    ("$id", artifact.Id.ToString()),
                    ("$run_id", artifact.RunId.ToString()),
                    ("$media", artifact.MediaType),
                    ("$locator", artifact.Locator),
                    ("$hash", (object?)artifact.Sha256Hex ?? DBNull.Value),
                    ("$at", artifact.CreatedAt.ToString("O")));
            }

            AddEvent(conn, tx, runId, endedAt, EventKind.RunEnded, new
            {
                status = finalStatus.ToString(),
                exitCode = result.ExitCode,
                durationMs = (int)duration.TotalMilliseconds,
                stdOutLines,
                stdErrLines,
                artifactCount = artifacts.Count,
                failureFingerprint,
                profileId = profile.Id,
                profileName = profile.Name
            });

            tx.Commit();
        }

        return runId;
    }

    private static ScriptRunSpec BuildSpecFromConfig(ThingConfig config, ThingProfile profile, string args)
    {
        // Working directory: profile override > config default > script directory
        var workingDir = profile.WorkingDir ?? config.WorkingDir;

        return new ScriptRunSpec(
            FilePath: config.Path,
            Arguments: args,
            WorkingDirectory: workingDir,
            Env: null);  // Env is added separately
    }

    private static string GetRunDirectory(RunId runId)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlRoom",
            "runs",
            runId.ToString());
        return baseDir;
    }

    /// <summary>
    /// Get the run directory path for a given run ID (static helper for UI)
    /// </summary>
    public static string GetRunDirectoryPath(RunId runId) => GetRunDirectory(runId);

    private static async Task<List<Artifact>> CaptureArtifactsAsync(RunId runId, string artifactDir)
    {
        var artifacts = new List<Artifact>();

        if (!Directory.Exists(artifactDir))
            return artifacts;

        var files = Directory.GetFiles(artifactDir, "*", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            var relativePath = Path.GetRelativePath(artifactDir, filePath);
            var mediaType = GetMediaType(filePath);
            var hash = await ComputeFileHashAsync(filePath);

            var artifact = new Artifact(
                Id: ArtifactId.New(),
                RunId: runId,
                MediaType: mediaType,
                Locator: filePath,
                Sha256Hex: hash,
                CreatedAt: File.GetCreationTimeUtc(filePath)
            );

            artifacts.Add(artifact);
        }

        return artifacts;
    }

    private static string GetMediaType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".csv" => "text/csv",
            ".log" => "text/plain",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Compute a failure fingerprint that groups similar failures together.
    /// Includes exit code and normalized stderr for robust grouping.
    /// </summary>
    private static string ComputeFailureFingerprint(int? exitCode, string stderr)
    {
        var normalized = NormalizeForFingerprint(stderr);
        var input = $"exit:{exitCode ?? -1}\n{normalized}";

        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Normalize stderr content for fingerprinting:
    /// - Take last N lines (most relevant for stack traces)
    /// - Strip timestamps
    /// - Collapse hex addresses, GUIDs, memory addresses
    /// - Normalize file paths
    /// - Collapse repeated whitespace
    /// </summary>
    private static string NormalizeForFingerprint(string content)
    {
        const int MaxLines = 50;

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Take last N lines (tail is usually most informative)
        var relevantLines = lines.Length > MaxLines
            ? lines[^MaxLines..]
            : lines;

        var normalized = new StringBuilder();

        foreach (var line in relevantLines)
        {
            var n = line;

            // Strip common timestamp patterns
            // [2024-01-29 14:30:45] or 2024-01-29T14:30:45.123Z or 14:30:45
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"\[?\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}[.\d]*Z?\]?", "[TIME]");
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"\b\d{2}:\d{2}:\d{2}[.,]\d+\b", "[TIME]");

            // Collapse GUIDs: 550e8400-e29b-41d4-a716-446655440000
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "[GUID]");

            // Collapse hex addresses: 0x7ffdf3a1b2c0 or 0x00007FF
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"0x[0-9a-fA-F]{6,16}", "[ADDR]");

            // Collapse memory addresses in stack traces: at 0x12345678
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"\bat\s+0x[0-9a-fA-F]+", "at [ADDR]");

            // Normalize Windows paths: C:\Users\someone\... -> [PATH]\...
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"[A-Z]:\\(?:Users|home)\\[^\\]+\\[^\s:]+", "[PATH]");

            // Normalize Unix paths: /home/user/... or /Users/name/...
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"/(?:home|Users)/[^/]+/[^\s:]+", "[PATH]");

            // Collapse line numbers in stack traces: :123 or line 123
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @":\d+(?::\d+)?(?=[\s\)]|$)", ":[LINE]");
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"\bline\s+\d+", "line [LINE]");

            // Collapse process IDs: PID 12345 or pid=12345
            n = System.Text.RegularExpressions.Regex.Replace(n,
                @"\b(?:PID|pid)[=:\s]*\d+", "PID [N]");

            // Collapse repeated whitespace
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ");

            normalized.AppendLine(n.Trim());
        }

        return normalized.ToString();
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static void AddEvent(SqliteConnection conn, SqliteTransaction tx, RunId runId, DateTimeOffset at, EventKind kind, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        Exec(conn, tx, """
            INSERT INTO run_events(run_id, at, kind, payload_json)
            VALUES ($run_id, $at, $kind, $payload)
            """,
            ("$run_id", runId.ToString()),
            ("$at", at.ToString("O")),
            ("$kind", (int)kind),
            ("$payload", json));
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in ps)
            cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }
}
