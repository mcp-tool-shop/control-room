using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlRoom.Application.UseCases;
using ControlRoom.Domain.Model;
using ControlRoom.Domain.Services;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

public partial class RunDetailViewModel : ObservableObject, IQueryAttributable
{
    private readonly RunQueries _runs;
    private readonly ArtifactQueries _artifacts;
    private readonly IAIAssistant? _aiAssistant;
    private CancellationTokenSource? _cts;
    private RunId _runId;
    private long _lastSeq;
    private bool _isComplete;
    private RunSummary? _parsedSummary;
    private string _thingName = "";
    private DateTimeOffset _startedAt;
    private string _errorOutput = "";

    public RunDetailViewModel(RunQueries runs, ArtifactQueries artifacts, IAIAssistant? aiAssistant = null)
    {
        _runs = runs;
        _artifacts = artifacts;
        _aiAssistant = aiAssistant;
    }

    [ObservableProperty]
    private string header = "Run";

    [ObservableProperty]
    private string status = "";

    [ObservableProperty]
    private string summaryText = "";

    [ObservableProperty]
    private string durationText = "";

    [ObservableProperty]
    private bool isRunning = true;

    [ObservableProperty]
    private bool hasArtifacts;

    [ObservableProperty]
    private bool hasRunDirectory;

    [ObservableProperty]
    private string commandLineText = "";

    [ObservableProperty]
    private bool isFailed;

    [ObservableProperty]
    private bool isAIAvailable;

    [ObservableProperty]
    private bool isAIAnalyzing;

    [ObservableProperty]
    private bool hasAIAnalysis;

    [ObservableProperty]
    private string aiSummary = "";

    [ObservableProperty]
    private string aiRootCause = "";

    [ObservableProperty]
    private string aiSeverity = "";

    [ObservableProperty]
    private double aiConfidence;

    [ObservableProperty]
    private string aiQuickFix = "";

    public ObservableCollection<LogLine> Lines { get; } = [];
    public ObservableCollection<ArtifactListItem> Artifacts { get; } = [];
    public ObservableCollection<string> AISuggestions { get; } = [];

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("runId", out var runIdObj) && runIdObj is string runIdStr)
        {
            _runId = new RunId(Guid.Parse(runIdStr));
            Header = $"Run {_runId.Value.ToString()[..8]}...";

            // Check if run directory exists
            var runDir = RunLocalScript.GetRunDirectoryPath(_runId);
            HasRunDirectory = Directory.Exists(runDir);

            StartTail();
        }
    }

    private void StartTail()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _lastSeq = 0;
        _isComplete = false;
        Lines.Clear();
        Artifacts.Clear();
        _ = TailLoopAsync(_cts.Token);
    }

    private async Task TailLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isComplete)
        {
            try
            {
                var events = await Task.Run(() => _runs.ListRunEvents(_runId, afterSeq: _lastSeq), ct);

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    foreach (var e in events)
                    {
                        _lastSeq = e.Seq;

                        if (e.Kind is EventKind.StdOut or EventKind.StdErr)
                        {
                            var line = ExtractLine(e.PayloadJson);
                            var isErr = e.Kind == EventKind.StdErr;
                            Lines.Add(new LogLine(line, isErr, e.At));

                            // Capture error output for AI analysis
                            if (isErr && !string.IsNullOrWhiteSpace(line))
                            {
                                _errorOutput += line + Environment.NewLine;
                            }
                        }
                        else if (e.Kind == EventKind.RunEnded)
                        {
                            _isComplete = true;
                            IsRunning = false;
                            ParseEndedEvent(e.PayloadJson);
                            LoadArtifacts();
                            LoadSummaryFromDb();
                            _ = CheckAIAvailabilityAsync();
                        }
                        else if (e.Kind == EventKind.RunStarted)
                        {
                            var thingName = ExtractThingName(e.PayloadJson);
                            if (!string.IsNullOrEmpty(thingName))
                            {
                                Header = thingName;
                                _thingName = thingName;
                            }
                            _startedAt = e.At;
                            Lines.Add(new LogLine("── Run started ──", false, e.At));
                        }
                    }
                });

                if (!_isComplete)
                    await Task.Delay(150, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void LoadSummaryFromDb()
    {
        // Load the full run record to get summary JSON
        var runs = _runs.ListRuns(limit: 500);
        var run = runs.FirstOrDefault(r => r.RunId == _runId);
        if (run is not null)
        {
            _parsedSummary = run.GetParsedSummary();
            if (_parsedSummary?.CommandLine is not null)
            {
                CommandLineText = _parsedSummary.CommandLine;
            }

            // Update run directory check
            var runDir = _parsedSummary?.RunDirectory ?? RunLocalScript.GetRunDirectoryPath(_runId);
            HasRunDirectory = Directory.Exists(runDir);
        }
    }

    private void ParseEndedEvent(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            // Extract status
            Status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "Unknown" : "Ended";
            IsFailed = Status == "Failed";

            // Extract duration
            if (root.TryGetProperty("durationMs", out var durationMs))
            {
                var ms = durationMs.GetInt32();
                DurationText = ms >= 1000 ? $"{ms / 1000.0:F1}s" : $"{ms}ms";
            }

            // Build summary text
            var parts = new List<string> { Status };

            if (!string.IsNullOrEmpty(DurationText))
                parts.Add(DurationText);

            if (root.TryGetProperty("stdOutLines", out var stdOut) && stdOut.GetInt32() > 0)
                parts.Add($"{stdOut.GetInt32()} lines");

            if (root.TryGetProperty("stdErrLines", out var stdErr) && stdErr.GetInt32() > 0)
                parts.Add($"{stdErr.GetInt32()} errors");

            if (root.TryGetProperty("artifactCount", out var artifacts) && artifacts.GetInt32() > 0)
                parts.Add($"{artifacts.GetInt32()} artifacts");

            SummaryText = string.Join(" · ", parts);

            // Add final line to log
            Lines.Add(new LogLine($"── {SummaryText} ──", false, DateTimeOffset.UtcNow));
        }
        catch
        {
            Status = "Ended";
            SummaryText = "Ended";
            Lines.Add(new LogLine("── Run ended ──", false, DateTimeOffset.UtcNow));
        }
    }

    private void LoadArtifacts()
    {
        var artifactList = _artifacts.ListArtifactsForRun(_runId);
        Artifacts.Clear();
        foreach (var artifact in artifactList)
        {
            Artifacts.Add(artifact);
        }
        HasArtifacts = Artifacts.Count > 0;
    }

    [RelayCommand]
    private void StopTailing()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Ask AI to analyze the error and suggest fixes
    /// </summary>
    [RelayCommand]
    private async Task AskAIAsync()
    {
        if (_aiAssistant is null || !IsFailed || IsAIAnalyzing)
            return;

        IsAIAnalyzing = true;
        AISuggestions.Clear();

        try
        {
            // Check if AI is available
            var available = await _aiAssistant.IsAvailableAsync();
            if (!available)
            {
                AiSummary = "AI assistant is not available. Ensure Ollama is running.";
                HasAIAnalysis = true;
                return;
            }

            // Build error context
            var context = new ErrorContext(
                ErrorOutput: _errorOutput,
                ExitCode: _parsedSummary?.ExitCode,
                ScriptPath: _parsedSummary?.CommandLine?.Split(' ').FirstOrDefault(),
                Arguments: _parsedSummary?.CommandLine,
                Environment: null,
                Duration: _parsedSummary?.Duration
            );

            // Get error explanation
            var explanation = await _aiAssistant.ExplainErrorAsync(context);
            AiSummary = explanation.Summary;
            AiRootCause = explanation.RootCause;
            AiSeverity = explanation.Severity.ToString();
            AiConfidence = explanation.Confidence;

            // Get fix suggestions
            var scriptContent = await TryReadScriptContentAsync();
            var fixes = await _aiAssistant.SuggestFixAsync(context, scriptContent);

            AiQuickFix = fixes.QuickFix ?? "";

            foreach (var suggestion in fixes.Suggestions.Take(5))
            {
                AISuggestions.Add($"• {suggestion.Title}: {suggestion.Description}");
            }

            HasAIAnalysis = true;
            Lines.Add(new LogLine("── AI analysis complete ──", false, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            AiSummary = $"AI analysis failed: {ex.Message}";
            HasAIAnalysis = true;
        }
        finally
        {
            IsAIAnalyzing = false;
        }
    }

    private async Task<string> TryReadScriptContentAsync()
    {
        var scriptPath = _parsedSummary?.CommandLine?.Split(' ').FirstOrDefault();
        if (!string.IsNullOrEmpty(scriptPath) && File.Exists(scriptPath))
        {
            try
            {
                return await File.ReadAllTextAsync(scriptPath);
            }
            catch
            {
                // Ignore read errors
            }
        }
        return "";
    }

    private async Task CheckAIAvailabilityAsync()
    {
        if (_aiAssistant is null)
        {
            IsAIAvailable = false;
            return;
        }

        try
        {
            IsAIAvailable = await _aiAssistant.IsAvailableAsync();
        }
        catch
        {
            IsAIAvailable = false;
        }
    }

    [RelayCommand]
    private async Task OpenArtifactAsync(ArtifactListItem? artifact)
    {
        if (artifact is null || !File.Exists(artifact.Locator))
            return;

        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(artifact.Locator)
            });
        }
        catch
        {
            // Fallback: open containing folder
            var folder = Path.GetDirectoryName(artifact.Locator);
            if (folder != null && Directory.Exists(folder))
            {
                await Launcher.Default.OpenAsync(folder);
            }
        }
    }

    /// <summary>
    /// Open the run's evidence directory in file explorer
    /// </summary>
    [RelayCommand]
    private async Task OpenRunFolderAsync()
    {
        var runDir = _parsedSummary?.RunDirectory ?? RunLocalScript.GetRunDirectoryPath(_runId);

        if (Directory.Exists(runDir))
        {
#if WINDOWS
            System.Diagnostics.Process.Start("explorer.exe", runDir);
#else
            await Launcher.Default.OpenAsync(runDir);
#endif
        }
        else
        {
            // Create the directory if it doesn't exist
            Directory.CreateDirectory(runDir);
#if WINDOWS
            System.Diagnostics.Process.Start("explorer.exe", runDir);
#else
            await Launcher.Default.OpenAsync(runDir);
#endif
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Export the run as a ZIP file (logs + artifacts + metadata)
    /// </summary>
    [RelayCommand]
    private async Task ExportAsZipAsync()
    {
        var runDir = _parsedSummary?.RunDirectory ?? RunLocalScript.GetRunDirectoryPath(_runId);

        // Determine export directory with fallback
        var exportDir = GetExportDirectory();

        // Safe filename: no colons or other problematic chars
        var safeThingName = SanitizeFilename(_thingName);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipName = $"run-{safeThingName}-{_runId.Value.ToString()[..8]}-{timestamp}.zip";
        var zipPath = Path.Combine(exportDir, zipName);

        try
        {
            // Create a temp directory to assemble the export
            var tempDir = Path.Combine(Path.GetTempPath(), $"controlroom-export-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Write comprehensive run metadata
                var metadata = new
                {
                    runId = _runId.ToString(),
                    thingName = _thingName,
                    startedAt = _startedAt.ToString("O"),
                    status = Status,
                    exitCode = _parsedSummary?.ExitCode,
                    duration = DurationText,
                    durationMs = _parsedSummary?.Duration.TotalMilliseconds,
                    stdOutLines = _parsedSummary?.StdOutLines ?? 0,
                    stdErrLines = _parsedSummary?.StdErrLines ?? 0,
                    artifactCount = _parsedSummary?.ArtifactCount ?? 0,
                    commandLine = _parsedSummary?.CommandLine,
                    workingDirectory = _parsedSummary?.WorkingDirectory,
                    runDirectory = runDir,
                    failureFingerprint = _parsedSummary?.FailureFingerprint,
                    lastStdErrLine = _parsedSummary?.LastStdErrLine,
                    copyableSummary = _parsedSummary?.ToCopyableString(_thingName, _startedAt) ?? SummaryText,
                    export = new
                    {
                        exportedAt = DateTimeOffset.UtcNow.ToString("O"),
                        appVersion = "1.0.0", // TODO: Read from assembly
                        osVersion = Environment.OSVersion.ToString(),
                        machineName = Environment.MachineName
                    }
                };
                var metadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(tempDir, "run-info.json"), metadataJson);

                // Write full log (human-readable)
                var logLines = Lines.Select(l => $"[{l.Timestamp:HH:mm:ss.fff}] {(l.IsError ? "ERR" : "OUT")} {l.Text}");
                await File.WriteAllLinesAsync(Path.Combine(tempDir, "output.log"), logLines);

                // Write events.jsonl (machine-readable, ordered)
                var events = _runs.ListRunEvents(_runId, afterSeq: 0);
                var eventsJsonl = events.Select(e => JsonSerializer.Serialize(new
                {
                    seq = e.Seq,
                    at = e.At.ToString("O"),
                    kind = e.Kind.ToString(),
                    payload = e.PayloadJson
                }));
                await File.WriteAllLinesAsync(Path.Combine(tempDir, "events.jsonl"), eventsJsonl);

                // Copy artifacts if they exist
                if (Directory.Exists(runDir))
                {
                    var artifactsDir = Path.Combine(tempDir, "artifacts");
                    Directory.CreateDirectory(artifactsDir);

                    foreach (var file in Directory.GetFiles(runDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(runDir, file);
                        var destPath = Path.Combine(artifactsDir, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.Copy(file, destPath, overwrite: true);
                    }
                }

                // Create the ZIP
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                ZipFile.CreateFromDirectory(tempDir, zipPath);

                // Visual feedback
                Lines.Add(new LogLine($"Exported to: {zipPath}", false, DateTimeOffset.UtcNow));

                // Open the containing folder with the ZIP selected
#if WINDOWS
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zipPath}\"");
#else
                await Launcher.Default.OpenAsync(exportDir);
#endif
            }
            finally
            {
                // Clean up temp directory
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Lines.Add(new LogLine($"Export failed: {ex.Message}", true, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Get export directory with fallback chain: Desktop → Documents → Temp
    /// </summary>
    private static string GetExportDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
            return desktop;

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs))
            return docs;

        return Path.GetTempPath();
    }

    /// <summary>
    /// Sanitize filename by replacing invalid characters
    /// </summary>
    private static string SanitizeFilename(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "run";

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        // Limit length
        if (sanitized.Length > 30)
            sanitized = sanitized[..30];

        return sanitized;
    }

    /// <summary>
    /// Copy a one-line summary to clipboard for sharing
    /// </summary>
    [RelayCommand]
    private async Task CopySummaryAsync()
    {
        string copyText;

        if (_parsedSummary is not null)
        {
            copyText = _parsedSummary.ToCopyableString(_thingName, _startedAt);
        }
        else
        {
            // Fallback to basic summary
            var statusIcon = Status switch
            {
                "Succeeded" => "✓",
                "Failed" => "✗",
                "Canceled" => "⊘",
                _ => "?"
            };
            copyText = $"{statusIcon} {_thingName} @ {_startedAt:yyyy-MM-dd HH:mm} · {SummaryText}";
        }

        await Clipboard.Default.SetTextAsync(copyText);

        // Visual feedback
        Lines.Add(new LogLine($"Copied: {copyText}", false, DateTimeOffset.UtcNow));
    }

    private static string ExtractLine(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("line", out var v) ? v.GetString() ?? "" : payloadJson;
        }
        catch
        {
            return payloadJson;
        }
    }

    private static string ExtractThingName(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("thingName", out var v) ? v.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }
}

public record LogLine(string Text, bool IsError, DateTimeOffset Timestamp);
