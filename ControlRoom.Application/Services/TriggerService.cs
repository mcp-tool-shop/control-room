using System.Collections.Concurrent;
using ControlRoom.Application.UseCases;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;
using NCrontab;

namespace ControlRoom.Application.Services;

/// <summary>
/// Service that manages runbook triggers: schedules, webhooks, and file watchers.
/// </summary>
public interface ITriggerService : IDisposable
{
    /// <summary>
    /// Start the trigger service (begins monitoring all enabled runbooks)
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the trigger service
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Reload triggers for a specific runbook (after edit)
    /// </summary>
    void ReloadRunbook(RunbookId runbookId);

    /// <summary>
    /// Trigger a webhook runbook by its secret
    /// </summary>
    Task<TriggerResult> TriggerWebhookAsync(string secret, string? payload = null);

    /// <summary>
    /// Get the next scheduled execution time for a runbook
    /// </summary>
    DateTimeOffset? GetNextScheduledRun(RunbookId runbookId);

    /// <summary>
    /// Get all active file watchers
    /// </summary>
    IReadOnlyList<FileWatcherInfo> GetActiveFileWatchers();

    /// <summary>
    /// Event raised when a trigger fires
    /// </summary>
    event EventHandler<TriggerFiredEventArgs>? TriggerFired;
}

/// <summary>
/// Result of triggering a runbook
/// </summary>
public sealed record TriggerResult(
    bool Success,
    RunbookExecutionId? ExecutionId,
    string? ErrorMessage
);

/// <summary>
/// Information about an active file watcher
/// </summary>
public sealed record FileWatcherInfo(
    RunbookId RunbookId,
    string RunbookName,
    string WatchPath,
    string Pattern,
    bool IsActive
);

/// <summary>
/// Event args when a trigger fires
/// </summary>
public sealed class TriggerFiredEventArgs : EventArgs
{
    public required RunbookId RunbookId { get; init; }
    public required string RunbookName { get; init; }
    public required TriggerType TriggerType { get; init; }
    public required DateTimeOffset FiredAt { get; init; }
    public RunbookExecutionId? ExecutionId { get; init; }
    public string? TriggerInfo { get; init; }
}

/// <summary>
/// Implementation of the trigger service
/// </summary>
public sealed class TriggerService : ITriggerService
{
    private readonly RunbookQueries _runbooks;
    private readonly IRunbookExecutor _executor;
    private readonly ILogger<TriggerService>? _logger;

    private readonly ConcurrentDictionary<RunbookId, ScheduleEntry> _schedules = new();
    private readonly ConcurrentDictionary<string, RunbookId> _webhookSecrets = new();
    private readonly ConcurrentDictionary<RunbookId, FileWatcherEntry> _fileWatchers = new();

    private CancellationTokenSource? _cts;
    private Task? _schedulerTask;
    private bool _disposed;

    public event EventHandler<TriggerFiredEventArgs>? TriggerFired;

    public TriggerService(RunbookQueries runbooks, IRunbookExecutor executor, ILogger<TriggerService>? logger = null)
    {
        _runbooks = runbooks;
        _executor = executor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null)
            throw new InvalidOperationException("Trigger service is already running");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Load all enabled runbooks and set up their triggers
        var runbooks = _runbooks.ListRunbooks(enabledOnly: true);

        foreach (var runbook in runbooks)
        {
            SetupTrigger(runbook.RunbookId);
        }

        // Start the scheduler loop
        _schedulerTask = RunSchedulerLoopAsync(_cts.Token);

        _logger?.LogInformation("Trigger service started with {Count} runbooks", runbooks.Count);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();

        if (_schedulerTask is not null)
        {
            try
            {
                await _schedulerTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        // Clean up file watchers
        foreach (var entry in _fileWatchers.Values)
        {
            entry.Watcher.Dispose();
        }
        _fileWatchers.Clear();
        _schedules.Clear();
        _webhookSecrets.Clear();

        _cts.Dispose();
        _cts = null;

        _logger?.LogInformation("Trigger service stopped");
    }

    public void ReloadRunbook(RunbookId runbookId)
    {
        // Remove existing triggers
        _schedules.TryRemove(runbookId, out _);

        if (_fileWatchers.TryRemove(runbookId, out var entry))
        {
            entry.Watcher.Dispose();
        }

        // Find and remove webhook secret
        var secretToRemove = _webhookSecrets
            .FirstOrDefault(kvp => kvp.Value == runbookId)
            .Key;
        if (secretToRemove is not null)
        {
            _webhookSecrets.TryRemove(secretToRemove, out _);
        }

        // Re-setup if runbook still exists and is enabled
        SetupTrigger(runbookId);
    }

    public async Task<TriggerResult> TriggerWebhookAsync(string secret, string? payload = null)
    {
        if (!_webhookSecrets.TryGetValue(secret, out var runbookId))
        {
            return new TriggerResult(false, null, "Invalid webhook secret");
        }

        var runbook = _runbooks.GetRunbook(runbookId);
        if (runbook is null)
        {
            return new TriggerResult(false, null, "Runbook not found");
        }

        if (!runbook.IsEnabled)
        {
            return new TriggerResult(false, null, "Runbook is disabled");
        }

        try
        {
            var executionId = await _executor.ExecuteAsync(runbookId);

            RaiseTriggerFired(runbook, TriggerType.Webhook, executionId, $"Payload size: {payload?.Length ?? 0}");

            return new TriggerResult(true, executionId, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to execute webhook-triggered runbook {RunbookId}", runbookId);
            return new TriggerResult(false, null, ex.Message);
        }
    }

    public DateTimeOffset? GetNextScheduledRun(RunbookId runbookId)
    {
        return _schedules.TryGetValue(runbookId, out var entry)
            ? entry.NextRun
            : null;
    }

    public IReadOnlyList<FileWatcherInfo> GetActiveFileWatchers()
    {
        return _fileWatchers.Values
            .Select(e => new FileWatcherInfo(
                e.RunbookId,
                e.RunbookName,
                e.WatchPath,
                e.Pattern,
                e.Watcher.EnableRaisingEvents))
            .ToList();
    }

    private void SetupTrigger(RunbookId runbookId)
    {
        var runbook = _runbooks.GetRunbook(runbookId);
        if (runbook is null || !runbook.IsEnabled) return;

        switch (runbook.Trigger)
        {
            case ScheduleTrigger schedule:
                SetupScheduleTrigger(runbook, schedule);
                break;

            case WebhookTrigger webhook:
                SetupWebhookTrigger(runbook, webhook);
                break;

            case FileWatchTrigger fileWatch:
                SetupFileWatchTrigger(runbook, fileWatch);
                break;
        }
    }

    private void SetupScheduleTrigger(Runbook runbook, ScheduleTrigger schedule)
    {
        try
        {
            var cron = CrontabSchedule.Parse(schedule.CronExpression, new CrontabSchedule.ParseOptions
            {
                IncludingSeconds = schedule.CronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6
            });

            var now = DateTime.UtcNow;
            var nextOccurrence = cron.GetNextOccurrence(now);

            var entry = new ScheduleEntry(
                runbook.Id,
                runbook.Name,
                cron,
                new DateTimeOffset(nextOccurrence, TimeSpan.Zero)
            );

            _schedules[runbook.Id] = entry;

            _logger?.LogDebug("Scheduled runbook {Name} for {NextRun}", runbook.Name, nextOccurrence);
        }
        catch (CrontabException ex)
        {
            _logger?.LogError(ex, "Invalid cron expression '{Cron}' for runbook {Name}",
                schedule.CronExpression, runbook.Name);
        }
    }

    private void SetupWebhookTrigger(Runbook runbook, WebhookTrigger webhook)
    {
        _webhookSecrets[webhook.Secret] = runbook.Id;
        _logger?.LogDebug("Registered webhook for runbook {Name}", runbook.Name);
    }

    private void SetupFileWatchTrigger(Runbook runbook, FileWatchTrigger fileWatch)
    {
        if (!Directory.Exists(fileWatch.Path))
        {
            _logger?.LogWarning("Watch path does not exist: {Path}", fileWatch.Path);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(fileWatch.Path)
            {
                Filter = fileWatch.Pattern,
                IncludeSubdirectories = fileWatch.IncludeSubdirectories,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };

            var debounce = fileWatch.Debounce ?? TimeSpan.FromSeconds(1);
            var lastTriggered = DateTimeOffset.MinValue;
            var lockObj = new object();

            void OnChanged(object sender, FileSystemEventArgs e)
            {
                lock (lockObj)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastTriggered < debounce)
                        return;
                    lastTriggered = now;
                }

                // Fire on thread pool to avoid blocking FileSystemWatcher
                Task.Run(async () =>
                {
                    try
                    {
                        var executionId = await _executor.ExecuteAsync(runbook.Id);
                        RaiseTriggerFired(runbook, TriggerType.FileWatch, executionId,
                            $"File: {e.Name}, Change: {e.ChangeType}");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to execute file-watch triggered runbook {Name}", runbook.Name);
                    }
                });
            }

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += (s, e) => OnChanged(s, e);
            watcher.EnableRaisingEvents = true;

            var entry = new FileWatcherEntry(
                runbook.Id,
                runbook.Name,
                fileWatch.Path,
                fileWatch.Pattern,
                watcher
            );

            _fileWatchers[runbook.Id] = entry;

            _logger?.LogDebug("Started file watcher for runbook {Name} on {Path}", runbook.Name, fileWatch.Path);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to setup file watcher for runbook {Name}", runbook.Name);
        }
    }

    private async Task RunSchedulerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

                var now = DateTimeOffset.UtcNow;

                foreach (var kvp in _schedules.ToArray())
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var entry = kvp.Value;
                    if (now >= entry.NextRun)
                    {
                        // Time to run!
                        try
                        {
                            var executionId = await _executor.ExecuteAsync(entry.RunbookId);

                            var runbook = _runbooks.GetRunbook(entry.RunbookId);
                            if (runbook is not null)
                            {
                                RaiseTriggerFired(runbook, TriggerType.Schedule, executionId,
                                    $"Scheduled run at {entry.NextRun:O}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to execute scheduled runbook {Name}", entry.RunbookName);
                        }

                        // Calculate next occurrence
                        var nextOccurrence = entry.Cron.GetNextOccurrence(DateTime.UtcNow);
                        var newEntry = entry with { NextRun = new DateTimeOffset(nextOccurrence, TimeSpan.Zero) };
                        _schedules[entry.RunbookId] = newEntry;

                        _logger?.LogDebug("Rescheduled runbook {Name} for {NextRun}",
                            entry.RunbookName, nextOccurrence);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in scheduler loop");
            }
        }
    }

    private void RaiseTriggerFired(Runbook runbook, TriggerType triggerType,
        RunbookExecutionId? executionId, string? triggerInfo)
    {
        TriggerFired?.Invoke(this, new TriggerFiredEventArgs
        {
            RunbookId = runbook.Id,
            RunbookName = runbook.Name,
            TriggerType = triggerType,
            FiredAt = DateTimeOffset.UtcNow,
            ExecutionId = executionId,
            TriggerInfo = triggerInfo
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAsync().GetAwaiter().GetResult();
    }

    private sealed record ScheduleEntry(
        RunbookId RunbookId,
        string RunbookName,
        CrontabSchedule Cron,
        DateTimeOffset NextRun
    );

    private sealed record FileWatcherEntry(
        RunbookId RunbookId,
        string RunbookName,
        string WatchPath,
        string Pattern,
        FileSystemWatcher Watcher
    ) : IDisposable
    {
        public void Dispose() => Watcher.Dispose();
    }
}

/// <summary>
/// Stub logger interface for when no logger is provided
/// </summary>
public interface ILogger<T>
{
    void LogInformation(string message, params object[] args);
    void LogDebug(string message, params object[] args);
    void LogWarning(string message, params object[] args);
    void LogError(Exception exception, string message, params object[] args);
}

/// <summary>
/// Null logger implementation
/// </summary>
public sealed class NullLogger<T> : ILogger<T>
{
    public static NullLogger<T> Instance { get; } = new();

    public void LogInformation(string message, params object[] args) { }
    public void LogDebug(string message, params object[] args) { }
    public void LogWarning(string message, params object[] args) { }
    public void LogError(Exception exception, string message, params object[] args) { }
}
