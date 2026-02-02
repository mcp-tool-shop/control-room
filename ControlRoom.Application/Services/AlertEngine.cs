using System.Collections.Concurrent;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.Services;

/// <summary>
/// Engine that evaluates alert rules against metrics and fires alerts
/// </summary>
public interface IAlertEngine : IDisposable
{
    /// <summary>
    /// Start the alert engine
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the alert engine
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Evaluate a specific rule immediately (for testing)
    /// </summary>
    AlertEvaluationResult EvaluateRule(AlertRuleId ruleId);

    /// <summary>
    /// Manually fire an alert (for testing or external triggers)
    /// </summary>
    AlertId FireAlert(AlertRuleId ruleId, double currentValue, string message);

    /// <summary>
    /// Get active alerts
    /// </summary>
    IReadOnlyList<Alert> GetActiveAlerts();

    /// <summary>
    /// Acknowledge an alert
    /// </summary>
    void AcknowledgeAlert(AlertId alertId);

    /// <summary>
    /// Resolve an alert
    /// </summary>
    void ResolveAlert(AlertId alertId);

    /// <summary>
    /// Event raised when an alert fires
    /// </summary>
    event EventHandler<AlertFiredEventArgs>? AlertFired;

    /// <summary>
    /// Event raised when an alert is resolved
    /// </summary>
    event EventHandler<AlertResolvedEventArgs>? AlertResolved;
}

/// <summary>
/// Result of evaluating an alert rule
/// </summary>
public sealed record AlertEvaluationResult(
    AlertRuleId RuleId,
    bool IsTriggered,
    double CurrentValue,
    double Threshold,
    string? Message
);

/// <summary>
/// Event args when an alert fires
/// </summary>
public sealed class AlertFiredEventArgs : EventArgs
{
    public required Alert Alert { get; init; }
    public required AlertRule Rule { get; init; }
}

/// <summary>
/// Event args when an alert is resolved
/// </summary>
public sealed class AlertResolvedEventArgs : EventArgs
{
    public required AlertId AlertId { get; init; }
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Implementation of the alert engine
/// </summary>
public sealed class AlertEngine : IAlertEngine
{
    private readonly MetricsQueries _metrics;
    private readonly ILogger<AlertEngine>? _logger;

    private readonly ConcurrentDictionary<AlertRuleId, DateTimeOffset> _lastFired = new();
    private readonly ConcurrentDictionary<AlertRuleId, AlertId> _activeAlertsByRule = new();

    private CancellationTokenSource? _cts;
    private Task? _evaluationTask;
    private bool _disposed;

    public event EventHandler<AlertFiredEventArgs>? AlertFired;
    public event EventHandler<AlertResolvedEventArgs>? AlertResolved;

    public AlertEngine(MetricsQueries metrics, ILogger<AlertEngine>? logger = null)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null)
            throw new InvalidOperationException("Alert engine is already running");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Load active alerts into tracking dictionary
        var activeAlerts = _metrics.GetActiveAlerts();
        foreach (var alert in activeAlerts)
        {
            _activeAlertsByRule[alert.RuleId] = alert.Id;
        }

        // Start the evaluation loop
        _evaluationTask = RunEvaluationLoopAsync(_cts.Token);

        _logger?.LogInformation("Alert engine started");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();

        if (_evaluationTask is not null)
        {
            try
            {
                await _evaluationTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _cts.Dispose();
        _cts = null;

        _logger?.LogInformation("Alert engine stopped");
    }

    public AlertEvaluationResult EvaluateRule(AlertRuleId ruleId)
    {
        var rules = _metrics.GetEnabledAlertRules();
        var rule = rules.FirstOrDefault(r => r.Id == ruleId);

        if (rule is null)
        {
            return new AlertEvaluationResult(ruleId, false, 0, 0, "Rule not found");
        }

        return EvaluateRuleInternal(rule);
    }

    public AlertId FireAlert(AlertRuleId ruleId, double currentValue, string message)
    {
        var rules = _metrics.GetEnabledAlertRules();
        var rule = rules.FirstOrDefault(r => r.Id == ruleId);

        if (rule is null)
        {
            throw new InvalidOperationException($"Rule not found: {ruleId}");
        }

        var alert = CreateAndFireAlert(rule, currentValue, message);
        return alert.Id;
    }

    public IReadOnlyList<Alert> GetActiveAlerts()
    {
        return _metrics.GetActiveAlerts();
    }

    public void AcknowledgeAlert(AlertId alertId)
    {
        _metrics.AcknowledgeAlert(alertId);
        _logger?.LogDebug("Alert {AlertId} acknowledged", alertId);
    }

    public void ResolveAlert(AlertId alertId)
    {
        var alerts = _metrics.GetActiveAlerts();
        var alert = alerts.FirstOrDefault(a => a.Id == alertId);

        if (alert is not null)
        {
            _metrics.ResolveAlert(alertId);

            // Remove from active tracking
            _activeAlertsByRule.TryRemove(alert.RuleId, out _);

            var duration = DateTimeOffset.UtcNow - alert.FiredAt;
            AlertResolved?.Invoke(this, new AlertResolvedEventArgs
            {
                AlertId = alertId,
                Duration = duration
            });

            _logger?.LogInformation("Alert {AlertId} resolved after {Duration}", alertId, duration);
        }
    }

    private async Task RunEvaluationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Evaluate every 15 seconds
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

                var rules = _metrics.GetEnabledAlertRules();

                foreach (var rule in rules)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        var result = EvaluateRuleInternal(rule);

                        if (result.IsTriggered)
                        {
                            HandleTriggeredRule(rule, result);
                        }
                        else
                        {
                            // Check if we should auto-resolve
                            HandleResolvedCondition(rule);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error evaluating rule {RuleId}", rule.Id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in alert evaluation loop");
            }
        }
    }

    private AlertEvaluationResult EvaluateRuleInternal(AlertRule rule)
    {
        var now = DateTimeOffset.UtcNow;
        var from = now - rule.EvaluationWindow;

        // Query metrics for the evaluation window
        var metrics = _metrics.QueryMetrics(rule.MetricName, from, now, rule.Tags, 10000);

        if (metrics.Count == 0)
        {
            return new AlertEvaluationResult(rule.Id, false, 0, rule.Threshold, "No data");
        }

        // Calculate the aggregate value based on condition type
        double currentValue = CalculateAggregateValue(metrics, rule.Condition);

        // Check the condition
        bool isTriggered = EvaluateCondition(currentValue, rule.Threshold, rule.Condition);

        string message = isTriggered
            ? $"{rule.MetricName} is {GetConditionDescription(rule.Condition)} {rule.Threshold} (current: {currentValue:F2})"
            : null!;

        return new AlertEvaluationResult(rule.Id, isTriggered, currentValue, rule.Threshold, message);
    }

    private double CalculateAggregateValue(IReadOnlyList<MetricPoint> metrics, AlertCondition condition)
    {
        return condition switch
        {
            // For change conditions, compare first and last values
            AlertCondition.AbsoluteChange =>
                Math.Abs(metrics.Last().Value - metrics.First().Value),

            AlertCondition.PercentChange when metrics.First().Value != 0 =>
                Math.Abs((metrics.Last().Value - metrics.First().Value) / metrics.First().Value * 100),

            // For anomaly detection, use Z-score
            AlertCondition.Anomaly => CalculateZScore(metrics),

            // For threshold conditions, use the latest value
            _ => metrics.Average(m => m.Value)
        };
    }

    private double CalculateZScore(IReadOnlyList<MetricPoint> metrics)
    {
        if (metrics.Count < 2) return 0;

        var values = metrics.Select(m => m.Value).ToList();
        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        var stdDev = Math.Sqrt(variance);

        if (stdDev == 0) return 0;

        // Calculate Z-score of the latest value
        return Math.Abs((metrics.Last().Value - mean) / stdDev);
    }

    private bool EvaluateCondition(double currentValue, double threshold, AlertCondition condition)
    {
        return condition switch
        {
            AlertCondition.GreaterThan => currentValue > threshold,
            AlertCondition.GreaterThanOrEqual => currentValue >= threshold,
            AlertCondition.LessThan => currentValue < threshold,
            AlertCondition.LessThanOrEqual => currentValue <= threshold,
            AlertCondition.Equal => Math.Abs(currentValue - threshold) < 0.0001,
            AlertCondition.NotEqual => Math.Abs(currentValue - threshold) >= 0.0001,
            AlertCondition.AbsoluteChange => currentValue > threshold,
            AlertCondition.PercentChange => currentValue > threshold,
            AlertCondition.Anomaly => currentValue > threshold, // threshold is Z-score limit (e.g., 3)
            _ => false
        };
    }

    private string GetConditionDescription(AlertCondition condition)
    {
        return condition switch
        {
            AlertCondition.GreaterThan => ">",
            AlertCondition.GreaterThanOrEqual => ">=",
            AlertCondition.LessThan => "<",
            AlertCondition.LessThanOrEqual => "<=",
            AlertCondition.Equal => "==",
            AlertCondition.NotEqual => "!=",
            AlertCondition.AbsoluteChange => "changed by >",
            AlertCondition.PercentChange => "changed by >",
            AlertCondition.Anomaly => "anomaly score >",
            _ => "?"
        };
    }

    private void HandleTriggeredRule(AlertRule rule, AlertEvaluationResult result)
    {
        var now = DateTimeOffset.UtcNow;

        // Check cooldown
        if (_lastFired.TryGetValue(rule.Id, out var lastFired))
        {
            if (now - lastFired < rule.CooldownPeriod)
            {
                _logger?.LogDebug("Rule {RuleId} in cooldown, skipping", rule.Id);
                return;
            }
        }

        // Check if alert already active for this rule
        if (_activeAlertsByRule.ContainsKey(rule.Id))
        {
            _logger?.LogDebug("Alert already active for rule {RuleId}", rule.Id);
            return;
        }

        // Fire new alert
        CreateAndFireAlert(rule, result.CurrentValue, result.Message!);
    }

    private Alert CreateAndFireAlert(AlertRule rule, double currentValue, string message)
    {
        var now = DateTimeOffset.UtcNow;

        var alert = new Alert(
            AlertId.New(),
            rule.Id,
            rule.Name,
            rule.Severity,
            message,
            currentValue,
            rule.Threshold,
            now,
            null,
            AlertStatus.Firing,
            rule.Tags
        );

        // Persist alert
        _metrics.FireAlert(alert);

        // Track
        _lastFired[rule.Id] = now;
        _activeAlertsByRule[rule.Id] = alert.Id;

        // Execute actions
        ExecuteAlertActions(rule, alert);

        // Raise event
        AlertFired?.Invoke(this, new AlertFiredEventArgs
        {
            Alert = alert,
            Rule = rule
        });

        _logger?.LogWarning("Alert fired: {AlertName} - {Message}", rule.Name, message);

        return alert;
    }

    private void ExecuteAlertActions(AlertRule rule, Alert alert)
    {
        foreach (var action in rule.Actions)
        {
            try
            {
                switch (action.Type)
                {
                    case AlertActionType.Notification:
                        // In-app notification - handled by event listeners
                        _logger?.LogDebug("Notification action for alert {AlertId}", alert.Id);
                        break;

                    case AlertActionType.Email:
                        // Would integrate with email service
                        _logger?.LogDebug("Email action for alert {AlertId} to {To}",
                            alert.Id, action.Config.GetValueOrDefault("to", "unknown"));
                        break;

                    case AlertActionType.Webhook:
                        // Would POST to webhook URL
                        if (action.Config.TryGetValue("url", out var url))
                        {
                            _logger?.LogDebug("Webhook action for alert {AlertId} to {Url}", alert.Id, url);
                            // TODO: Implement HTTP POST
                        }
                        break;

                    case AlertActionType.RunRunbook:
                        // Would trigger runbook execution
                        if (action.Config.TryGetValue("runbook_id", out var runbookId))
                        {
                            _logger?.LogDebug("RunRunbook action for alert {AlertId}: {RunbookId}",
                                alert.Id, runbookId);
                            // TODO: Integrate with IRunbookExecutor
                        }
                        break;

                    case AlertActionType.Script:
                        // Would execute custom script
                        _logger?.LogDebug("Script action for alert {AlertId}", alert.Id);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing alert action {ActionType} for {AlertId}",
                    action.Type, alert.Id);
            }
        }
    }

    private void HandleResolvedCondition(AlertRule rule)
    {
        // Auto-resolve if condition is no longer met
        if (_activeAlertsByRule.TryGetValue(rule.Id, out var alertId))
        {
            ResolveAlert(alertId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAsync().GetAwaiter().GetResult();
    }
}
