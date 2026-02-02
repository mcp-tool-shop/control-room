using System.Collections.Concurrent;
using System.Text.Json;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.UseCases;

/// <summary>
/// Interface for executing runbooks
/// </summary>
public interface IRunbookExecutor
{
    /// <summary>
    /// Execute a runbook by ID and return the execution ID
    /// </summary>
    Task<RunbookExecutionId> ExecuteAsync(
        RunbookId runbookId,
        string? triggerInfo = null,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a runbook and return the execution ID
    /// </summary>
    Task<RunbookExecutionId> ExecuteAsync(
        Runbook runbook,
        string? triggerInfo = null,
        CancellationToken ct = default);

    /// <summary>
    /// Pause a running execution
    /// </summary>
    Task PauseAsync(RunbookExecutionId executionId);

    /// <summary>
    /// Resume a paused execution
    /// </summary>
    Task ResumeAsync(RunbookExecutionId executionId);

    /// <summary>
    /// Cancel a running or paused execution
    /// </summary>
    Task CancelAsync(RunbookExecutionId executionId);

    /// <summary>
    /// Get current status of an execution
    /// </summary>
    RunbookExecutionInfo? GetExecutionInfo(RunbookExecutionId executionId);

    /// <summary>
    /// Event raised when a step completes
    /// </summary>
    event EventHandler<StepCompletedEventArgs>? StepCompleted;

    /// <summary>
    /// Event raised when execution status changes
    /// </summary>
    event EventHandler<ExecutionStatusChangedEventArgs>? StatusChanged;
}

/// <summary>
/// Information about a running execution
/// </summary>
public sealed record RunbookExecutionInfo(
    RunbookExecutionId ExecutionId,
    RunbookId RunbookId,
    RunbookExecutionStatus Status,
    DateTimeOffset StartedAt,
    IReadOnlyDictionary<string, StepExecutionStatus> StepStatuses,
    bool IsPaused
);

/// <summary>
/// Event args for step completion
/// </summary>
public sealed class StepCompletedEventArgs : EventArgs
{
    public required RunbookExecutionId ExecutionId { get; init; }
    public required string StepId { get; init; }
    public required string StepName { get; init; }
    public required StepExecutionStatus Status { get; init; }
    public required RunId? RunId { get; init; }
    public required TimeSpan? Duration { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Event args for execution status changes
/// </summary>
public sealed class ExecutionStatusChangedEventArgs : EventArgs
{
    public required RunbookExecutionId ExecutionId { get; init; }
    public required RunbookExecutionStatus OldStatus { get; init; }
    public required RunbookExecutionStatus NewStatus { get; init; }
}

/// <summary>
/// Executes runbooks with parallel step execution respecting dependencies
/// </summary>
public sealed class RunbookExecutor : IRunbookExecutor
{
    private readonly RunLocalScript _scriptRunner;
    private readonly ThingQueries _thingQueries;
    private readonly RunbookQueries _runbookQueries;
    private readonly ConcurrentDictionary<RunbookExecutionId, ExecutionState> _activeExecutions = new();

    public event EventHandler<StepCompletedEventArgs>? StepCompleted;
    public event EventHandler<ExecutionStatusChangedEventArgs>? StatusChanged;

    public RunbookExecutor(
        RunLocalScript scriptRunner,
        ThingQueries thingQueries,
        RunbookQueries runbookQueries)
    {
        _scriptRunner = scriptRunner;
        _thingQueries = thingQueries;
        _runbookQueries = runbookQueries;
    }

    public async Task<RunbookExecutionId> ExecuteAsync(
        RunbookId runbookId,
        string? triggerInfo = null,
        CancellationToken ct = default)
    {
        var runbook = _runbookQueries.GetRunbook(runbookId);
        if (runbook is null)
        {
            throw new InvalidOperationException($"Runbook not found: {runbookId}");
        }
        return await ExecuteAsync(runbook, triggerInfo, ct);
    }

    public async Task<RunbookExecutionId> ExecuteAsync(
        Runbook runbook,
        string? triggerInfo = null,
        CancellationToken ct = default)
    {
        // Validate runbook first
        var validation = runbook.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Runbook validation failed: {string.Join("; ", validation.Errors)}");
        }

        var executionId = RunbookExecutionId.New();
        var startedAt = DateTimeOffset.UtcNow;

        // Create initial step executions
        var stepExecutions = runbook.Steps.Select(step => new StepExecution(
            step.StepId,
            step.Name,
            RunId: null,
            StepExecutionStatus.Pending,
            StartedAt: null,
            EndedAt: null,
            Attempt: 0,
            ErrorMessage: null,
            Output: null
        )).ToList();

        // Create execution record
        var execution = new RunbookExecution(
            executionId,
            runbook.Id,
            RunbookExecutionStatus.Running,
            startedAt,
            EndedAt: null,
            stepExecutions,
            triggerInfo,
            ErrorMessage: null
        );

        // Persist execution
        _runbookQueries.InsertExecution(execution);

        // Create execution state
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var state = new ExecutionState(
            executionId,
            runbook,
            cts,
            startedAt
        );

        _activeExecutions[executionId] = state;

        // Execute in background
        _ = ExecuteInternalAsync(state).ContinueWith(t =>
        {
            _activeExecutions.TryRemove(executionId, out _);
            state.Dispose();
        }, TaskScheduler.Default);

        return executionId;
    }

    private async Task ExecuteInternalAsync(ExecutionState state)
    {
        var runbook = state.Runbook;
        var executionId = state.ExecutionId;
        var ct = state.CancellationTokenSource.Token;

        try
        {
            // Get topological order for execution
            var orderedSteps = runbook.GetTopologicalOrder();
            var stepMap = orderedSteps.ToDictionary(s => s.StepId);

            // Track completed steps
            var completed = new ConcurrentDictionary<string, StepExecutionStatus>();
            var runningSteps = new ConcurrentDictionary<string, Task>();

            // Process steps in waves (respecting dependencies)
            while (completed.Count < orderedSteps.Count)
            {
                ct.ThrowIfCancellationRequested();

                // Check for pause
                while (state.IsPaused)
                {
                    await Task.Delay(100, ct);
                }

                // Find steps that can run (all dependencies met)
                var readySteps = orderedSteps
                    .Where(s => !completed.ContainsKey(s.StepId) && !runningSteps.ContainsKey(s.StepId))
                    .Where(s => s.DependsOn.All(d => completed.ContainsKey(d)))
                    .ToList();

                if (readySteps.Count == 0 && runningSteps.IsEmpty)
                {
                    // Deadlock or all remaining steps skipped
                    break;
                }

                // Start ready steps in parallel
                foreach (var step in readySteps)
                {
                    var task = ExecuteStepAsync(state, step, completed, ct);
                    runningSteps[step.StepId] = task;
                }

                // Wait for at least one step to complete
                if (!runningSteps.IsEmpty)
                {
                    var completedTask = await Task.WhenAny(runningSteps.Values);
                    // Remove completed tasks
                    foreach (var kvp in runningSteps.ToArray())
                    {
                        if (kvp.Value.IsCompleted)
                        {
                            runningSteps.TryRemove(kvp.Key, out _);
                        }
                    }
                }
            }

            // Wait for any remaining steps
            await Task.WhenAll(runningSteps.Values);

            // Determine final status
            var hasFailures = completed.Values.Any(s => s == StepExecutionStatus.Failed);
            var hasSuccesses = completed.Values.Any(s => s == StepExecutionStatus.Succeeded);
            var allSucceeded = completed.Values.All(s => s == StepExecutionStatus.Succeeded);

            var finalStatus = allSucceeded
                ? RunbookExecutionStatus.Succeeded
                : hasSuccesses && hasFailures
                    ? RunbookExecutionStatus.PartialSuccess
                    : RunbookExecutionStatus.Failed;

            UpdateExecutionStatus(state, finalStatus);
        }
        catch (OperationCanceledException)
        {
            UpdateExecutionStatus(state, RunbookExecutionStatus.Canceled);
        }
        catch (Exception ex)
        {
            UpdateExecutionStatus(state, RunbookExecutionStatus.Failed, ex.Message);
        }
    }

    private async Task ExecuteStepAsync(
        ExecutionState state,
        RunbookStep step,
        ConcurrentDictionary<string, StepExecutionStatus> completed,
        CancellationToken ct)
    {
        var executionId = state.ExecutionId;
        var startedAt = DateTimeOffset.UtcNow;

        // Check if step should execute based on conditions
        if (!step.ShouldExecute(completed.ToDictionary(k => k.Key, v => v.Value)))
        {
            // Skip this step
            _runbookQueries.UpdateStepExecution(
                executionId,
                step.StepId,
                StepExecutionStatus.Skipped,
                startedAt: startedAt,
                endedAt: DateTimeOffset.UtcNow
            );

            completed[step.StepId] = StepExecutionStatus.Skipped;

            StepCompleted?.Invoke(this, new StepCompletedEventArgs
            {
                ExecutionId = executionId,
                StepId = step.StepId,
                StepName = step.Name,
                Status = StepExecutionStatus.Skipped,
                RunId = null,
                Duration = TimeSpan.Zero
            });

            return;
        }

        // Mark step as running
        _runbookQueries.UpdateStepExecution(
            executionId,
            step.StepId,
            StepExecutionStatus.Running,
            startedAt: startedAt,
            attempt: 1
        );

        // Execute with retry policy
        var maxAttempts = step.Retry?.MaxAttempts ?? 1;
        var lastError = "";
        RunId? runId = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Get the Thing to execute
                var thing = _thingQueries.GetThing(step.ThingId);
                if (thing is null)
                {
                    throw new InvalidOperationException($"Thing not found: {step.ThingId}");
                }

                // Create Thing record for execution
                var thingRecord = new Thing(
                    thing.ThingId,
                    thing.Name,
                    thing.Kind,
                    thing.ConfigJson,
                    DateTimeOffset.UtcNow
                );

                // Apply timeout if configured
                using var timeoutCts = step.Timeout.HasValue
                    ? new CancellationTokenSource(step.Timeout.Value)
                    : new CancellationTokenSource();

                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                // Execute the script
                runId = await _scriptRunner.ExecuteWithProfileAsync(
                    thingRecord,
                    step.ProfileId,
                    step.ArgumentsOverride,
                    combinedCts.Token
                );

                // Check if run succeeded (need to query the result)
                // For now, if ExecuteWithProfileAsync returns without throwing, we assume success
                // A more robust implementation would check the Run's status

                var endedAt = DateTimeOffset.UtcNow;

                _runbookQueries.UpdateStepExecution(
                    executionId,
                    step.StepId,
                    StepExecutionStatus.Succeeded,
                    runId: runId,
                    endedAt: endedAt,
                    attempt: attempt
                );

                completed[step.StepId] = StepExecutionStatus.Succeeded;

                StepCompleted?.Invoke(this, new StepCompletedEventArgs
                {
                    ExecutionId = executionId,
                    StepId = step.StepId,
                    StepName = step.Name,
                    Status = StepExecutionStatus.Succeeded,
                    RunId = runId,
                    Duration = endedAt - startedAt
                });

                return; // Success - exit retry loop
            }
            catch (Exception ex)
            {
                lastError = ex.Message;

                if (attempt < maxAttempts)
                {
                    // Wait before retry with exponential backoff
                    var delay = step.Retry?.GetDelay(attempt) ?? TimeSpan.FromSeconds(5);
                    await Task.Delay(delay, ct);

                    _runbookQueries.UpdateStepExecution(
                        executionId,
                        step.StepId,
                        StepExecutionStatus.Running,
                        attempt: attempt + 1,
                        errorMessage: $"Retry {attempt + 1}/{maxAttempts}: {ex.Message}"
                    );
                }
            }
        }

        // All retries exhausted - mark as failed
        var failedAt = DateTimeOffset.UtcNow;

        _runbookQueries.UpdateStepExecution(
            executionId,
            step.StepId,
            StepExecutionStatus.Failed,
            runId: runId,
            endedAt: failedAt,
            errorMessage: lastError
        );

        completed[step.StepId] = StepExecutionStatus.Failed;

        StepCompleted?.Invoke(this, new StepCompletedEventArgs
        {
            ExecutionId = executionId,
            StepId = step.StepId,
            StepName = step.Name,
            Status = StepExecutionStatus.Failed,
            RunId = runId,
            Duration = failedAt - startedAt,
            ErrorMessage = lastError
        });
    }

    private void UpdateExecutionStatus(
        ExecutionState state,
        RunbookExecutionStatus newStatus,
        string? errorMessage = null)
    {
        var oldStatus = state.Status;
        state.Status = newStatus;

        _runbookQueries.UpdateExecutionStatus(
            state.ExecutionId,
            newStatus,
            DateTimeOffset.UtcNow,
            errorMessage
        );

        StatusChanged?.Invoke(this, new ExecutionStatusChangedEventArgs
        {
            ExecutionId = state.ExecutionId,
            OldStatus = oldStatus,
            NewStatus = newStatus
        });
    }

    public Task PauseAsync(RunbookExecutionId executionId)
    {
        if (_activeExecutions.TryGetValue(executionId, out var state))
        {
            if (state.Status == RunbookExecutionStatus.Running)
            {
                state.IsPaused = true;
                UpdateExecutionStatus(state, RunbookExecutionStatus.Paused);
            }
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(RunbookExecutionId executionId)
    {
        if (_activeExecutions.TryGetValue(executionId, out var state))
        {
            if (state.Status == RunbookExecutionStatus.Paused)
            {
                state.IsPaused = false;
                UpdateExecutionStatus(state, RunbookExecutionStatus.Running);
            }
        }
        return Task.CompletedTask;
    }

    public Task CancelAsync(RunbookExecutionId executionId)
    {
        if (_activeExecutions.TryGetValue(executionId, out var state))
        {
            state.CancellationTokenSource.Cancel();
        }
        return Task.CompletedTask;
    }

    public RunbookExecutionInfo? GetExecutionInfo(RunbookExecutionId executionId)
    {
        if (_activeExecutions.TryGetValue(executionId, out var state))
        {
            return new RunbookExecutionInfo(
                state.ExecutionId,
                state.Runbook.Id,
                state.Status,
                state.StartedAt,
                state.StepStatuses.ToDictionary(k => k.Key, v => v.Value),
                state.IsPaused
            );
        }

        // Check database for completed execution
        var execution = _runbookQueries.GetExecution(executionId);
        if (execution is not null)
        {
            return new RunbookExecutionInfo(
                execution.Id,
                execution.RunbookId,
                execution.Status,
                execution.StartedAt,
                execution.StepExecutions.ToDictionary(s => s.StepId, s => s.Status),
                IsPaused: false
            );
        }

        return null;
    }

    /// <summary>
    /// Internal state for an active execution
    /// </summary>
    private sealed class ExecutionState : IDisposable
    {
        public RunbookExecutionId ExecutionId { get; }
        public Runbook Runbook { get; }
        public CancellationTokenSource CancellationTokenSource { get; }
        public DateTimeOffset StartedAt { get; }
        public RunbookExecutionStatus Status { get; set; } = RunbookExecutionStatus.Running;
        public bool IsPaused { get; set; }
        public ConcurrentDictionary<string, StepExecutionStatus> StepStatuses { get; } = new();

        public ExecutionState(
            RunbookExecutionId executionId,
            Runbook runbook,
            CancellationTokenSource cts,
            DateTimeOffset startedAt)
        {
            ExecutionId = executionId;
            Runbook = runbook;
            CancellationTokenSource = cts;
            StartedAt = startedAt;

            // Initialize all steps as pending
            foreach (var step in runbook.Steps)
            {
                StepStatuses[step.StepId] = StepExecutionStatus.Pending;
            }
        }

        public void Dispose()
        {
            CancellationTokenSource.Dispose();
        }
    }
}
