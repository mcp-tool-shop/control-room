using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlRoom.Application.Services;
using ControlRoom.Domain.Model;

namespace ControlRoom.App.ViewModels;

/// <summary>
/// View model for the system status page
/// </summary>
public partial class StatusPageViewModel : ObservableObject
{
    private readonly IHealthCheckService _healthCheckService;
    private readonly IAlertEngine _alertEngine;
    private readonly ISelfHealingEngine _selfHealingEngine;

    private IDispatcherTimer? _refreshTimer;

    public StatusPageViewModel(
        IHealthCheckService healthCheckService,
        IAlertEngine alertEngine,
        ISelfHealingEngine selfHealingEngine)
    {
        _healthCheckService = healthCheckService;
        _alertEngine = alertEngine;
        _selfHealingEngine = selfHealingEngine;

        // Subscribe to events
        _healthCheckService.StatusChanged += OnHealthCheckStatusChanged;
        _healthCheckService.SystemHealthChanged += OnSystemHealthChanged;
        _alertEngine.AlertFired += OnAlertFired;
        _alertEngine.AlertResolved += OnAlertResolved;
    }

    public ObservableCollection<HealthCheckViewModel> HealthChecks { get; } = [];
    public ObservableCollection<StatusAlertViewModel> ActiveAlerts { get; } = [];
    public ObservableCollection<SelfHealingExecutionViewModel> RecentRemediations { get; } = [];

    [ObservableProperty]
    private HealthStatus overallStatus = HealthStatus.Unknown;

    [ObservableProperty]
    private string overallStatusText = "Unknown";

    [ObservableProperty]
    private string overallStatusColor = "#9E9E9E";

    [ObservableProperty]
    private int healthyCount;

    [ObservableProperty]
    private int degradedCount;

    [ObservableProperty]
    private int unhealthyCount;

    [ObservableProperty]
    private int totalChecks;

    [ObservableProperty]
    private double healthyPercentage;

    [ObservableProperty]
    private int activeAlertCount;

    [ObservableProperty]
    private int criticalAlertCount;

    [ObservableProperty]
    private bool isRefreshing;

    [ObservableProperty]
    private DateTimeOffset lastUpdated = DateTimeOffset.UtcNow;

    [ObservableProperty]
    private bool autoRefreshEnabled = true;

    public async Task LoadAsync()
    {
        await RefreshAsync();

        // Start auto-refresh timer
        if (_refreshTimer is null)
        {
            _refreshTimer = Microsoft.Maui.Controls.Application.Current?.Dispatcher.CreateTimer();
            if (_refreshTimer is not null)
            {
                _refreshTimer.Interval = TimeSpan.FromSeconds(15);
                _refreshTimer.Tick += async (s, e) =>
                {
                    if (AutoRefreshEnabled)
                        await RefreshAsync();
                };
                _refreshTimer.Start();
            }
        }
    }

    public void Cleanup()
    {
        _refreshTimer?.Stop();
        _healthCheckService.StatusChanged -= OnHealthCheckStatusChanged;
        _healthCheckService.SystemHealthChanged -= OnSystemHealthChanged;
        _alertEngine.AlertFired -= OnAlertFired;
        _alertEngine.AlertResolved -= OnAlertResolved;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;

        await Task.Run(() =>
        {
            var systemHealth = _healthCheckService.GetSystemHealth();
            var alerts = _alertEngine.GetActiveAlerts();
            var remediations = _selfHealingEngine.GetRecentExecutions(10);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Update overall status
                OverallStatus = systemHealth.OverallStatus;
                OverallStatusText = systemHealth.OverallStatus.ToString();
                OverallStatusColor = GetStatusColor(systemHealth.OverallStatus);
                HealthyCount = systemHealth.HealthyCount;
                DegradedCount = systemHealth.DegradedCount;
                UnhealthyCount = systemHealth.UnhealthyCount;
                TotalChecks = systemHealth.TotalChecks;
                HealthyPercentage = systemHealth.HealthyPercentage;

                // Update health checks
                HealthChecks.Clear();
                foreach (var result in systemHealth.Results)
                {
                    HealthChecks.Add(new HealthCheckViewModel(result));
                }

                // Update alerts
                ActiveAlertCount = alerts.Count;
                CriticalAlertCount = alerts.Count(a => a.Severity == AlertSeverity.Critical);

                ActiveAlerts.Clear();
                foreach (var alert in alerts.Take(10))
                {
                    ActiveAlerts.Add(new StatusAlertViewModel(alert));
                }

                // Update remediations
                RecentRemediations.Clear();
                foreach (var remediation in remediations)
                {
                    RecentRemediations.Add(new SelfHealingExecutionViewModel(remediation));
                }

                LastUpdated = DateTimeOffset.UtcNow;
            });
        });

        IsRefreshing = false;
    }

    [RelayCommand]
    private async Task ExecuteHealthCheckAsync(HealthCheckViewModel? check)
    {
        if (check is null) return;

        try
        {
            await _healthCheckService.ExecuteCheckAsync(check.CheckId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to execute health check: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private async Task AcknowledgeAlertAsync(StatusAlertViewModel? alert)
    {
        if (alert is null) return;

        _alertEngine.AcknowledgeAlert(alert.AlertId);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ResolveAlertAsync(StatusAlertViewModel? alert)
    {
        if (alert is null) return;

        _alertEngine.ResolveAlert(alert.AlertId);
        await RefreshAsync();
    }

    [RelayCommand]
    private void ToggleAutoRefresh()
    {
        AutoRefreshEnabled = !AutoRefreshEnabled;
    }

    private void OnHealthCheckStatusChanged(object? sender, HealthCheckStatusChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await RefreshAsync();
        });
    }

    private void OnSystemHealthChanged(object? sender, SystemHealthChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OverallStatus = e.NewStatus;
            OverallStatusText = e.NewStatus.ToString();
            OverallStatusColor = GetStatusColor(e.NewStatus);
        });
    }

    private void OnAlertFired(object? sender, AlertFiredEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await RefreshAsync();
        });
    }

    private void OnAlertResolved(object? sender, AlertResolvedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await RefreshAsync();
        });
    }

    private static string GetStatusColor(HealthStatus status)
    {
        return status switch
        {
            HealthStatus.Healthy => "#4CAF50",    // Green
            HealthStatus.Degraded => "#FF9800",   // Orange
            HealthStatus.Unhealthy => "#F44336",  // Red
            _ => "#9E9E9E"                        // Grey
        };
    }
}

/// <summary>
/// View model for individual health check display
/// </summary>
public partial class HealthCheckViewModel : ObservableObject
{
    private readonly HealthCheckResult _result;

    public HealthCheckViewModel(HealthCheckResult result)
    {
        _result = result;
    }

    public HealthCheckId CheckId => _result.CheckId;
    public string Name => _result.CheckName;
    public HealthStatus Status => _result.Status;
    public string StatusText => _result.Status.ToString();

    public string StatusColor => _result.Status switch
    {
        HealthStatus.Healthy => "#4CAF50",
        HealthStatus.Degraded => "#FF9800",
        HealthStatus.Unhealthy => "#F44336",
        _ => "#9E9E9E"
    };

    public string StatusIcon => _result.Status switch
    {
        HealthStatus.Healthy => "✓",
        HealthStatus.Degraded => "⚠",
        HealthStatus.Unhealthy => "✗",
        _ => "?"
    };

    public string Message => _result.Message ?? "";
    public string ResponseTime => $"{_result.ResponseTime.TotalMilliseconds:F0}ms";
    public string CheckedAt => GetRelativeTime(_result.CheckedAt);

    private static string GetRelativeTime(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return time.ToString("MMM d HH:mm");
    }
}

/// <summary>
/// View model for alert display
/// </summary>
public partial class StatusAlertViewModel : ObservableObject
{
    private readonly Alert _alert;

    public StatusAlertViewModel(Alert alert)
    {
        _alert = alert;
    }

    public AlertId AlertId => _alert.Id;
    public string RuleName => _alert.RuleName;
    public AlertSeverity Severity => _alert.Severity;
    public string SeverityText => _alert.Severity.ToString();

    public string SeverityColor => _alert.Severity switch
    {
        AlertSeverity.Critical => "#D32F2F",
        AlertSeverity.Error => "#F44336",
        AlertSeverity.Warning => "#FF9800",
        _ => "#2196F3"
    };

    public string SeverityIcon => _alert.Severity switch
    {
        AlertSeverity.Critical => "🔴",
        AlertSeverity.Error => "🟠",
        AlertSeverity.Warning => "🟡",
        _ => "🔵"
    };

    public string Message => _alert.Message;
    public string CurrentValue => $"{_alert.CurrentValue:F2}";
    public string Threshold => $"{_alert.Threshold:F2}";
    public string FiredAt => GetRelativeTime(_alert.FiredAt);

    public string Duration
    {
        get
        {
            var dur = _alert.Duration;
            if (dur is null) return "";
            if (dur.Value.TotalMinutes < 1) return $"{(int)dur.Value.TotalSeconds}s";
            if (dur.Value.TotalHours < 1) return $"{(int)dur.Value.TotalMinutes}m";
            return $"{(int)dur.Value.TotalHours}h {dur.Value.Minutes}m";
        }
    }

    public bool IsAcknowledged => _alert.Status == AlertStatus.Acknowledged;

    private static string GetRelativeTime(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return time.ToString("MMM d HH:mm");
    }
}

/// <summary>
/// View model for self-healing execution display
/// </summary>
public partial class SelfHealingExecutionViewModel : ObservableObject
{
    private readonly SelfHealingExecution _execution;

    public SelfHealingExecutionViewModel(SelfHealingExecution execution)
    {
        _execution = execution;
    }

    public SelfHealingExecutionId ExecutionId => _execution.Id;
    public SelfHealingStatus Status => _execution.Status;
    public string StatusText => _execution.Status.ToString();

    public string StatusColor => _execution.Status switch
    {
        SelfHealingStatus.Succeeded => "#4CAF50",
        SelfHealingStatus.Failed => "#F44336",
        SelfHealingStatus.Running => "#2196F3",
        SelfHealingStatus.AwaitingApproval => "#FF9800",
        _ => "#9E9E9E"
    };

    public string StatusIcon => _execution.Status switch
    {
        SelfHealingStatus.Succeeded => "✓",
        SelfHealingStatus.Failed => "✗",
        SelfHealingStatus.Running => "⟳",
        SelfHealingStatus.AwaitingApproval => "⏳",
        SelfHealingStatus.Skipped => "⊘",
        _ => "○"
    };

    public bool HasTriggeringAlert => _execution.TriggeringAlert.HasValue;
    public string TriggeringAlertId => _execution.TriggeringAlert?.ToString() ?? "";
    public string StartedAt => GetRelativeTime(_execution.StartedAt);

    public string Duration
    {
        get
        {
            var end = _execution.CompletedAt ?? DateTimeOffset.UtcNow;
            var dur = end - _execution.StartedAt;
            if (dur.TotalMinutes < 1) return $"{(int)dur.TotalSeconds}s";
            return $"{(int)dur.TotalMinutes}m {dur.Seconds}s";
        }
    }

    public string Result => _execution.Result ?? "";

    private static string GetRelativeTime(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return time.ToString("MMM d HH:mm");
    }
}
