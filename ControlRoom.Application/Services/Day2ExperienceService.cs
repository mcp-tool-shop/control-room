namespace ControlRoom.Application.Services;

/// <summary>
/// Day-2 Experience: Ensures the app remains useful and performant over time,
/// with manageable data, tunable alerts, and safe team evolution.
///
/// Checklist items addressed:
/// - Old data manageable
/// - Performance stable over time
/// - Alerts and feeds remain useful
/// - Easy to tune verbosity
/// - Teams evolve safely
/// - Config drift detectable
/// </summary>
public sealed class Day2ExperienceService
{
    private readonly IDataMaintenanceRepository _dataRepository;
    private readonly IAlertTuningRepository _alertRepository;
    private readonly IConfigDriftRepository _driftRepository;

    public event EventHandler<MaintenanceRecommendationEventArgs>? MaintenanceRecommended;
    public event EventHandler<ConfigDriftDetectedEventArgs>? ConfigDriftDetected;

    public Day2ExperienceService(
        IDataMaintenanceRepository dataRepository,
        IAlertTuningRepository alertRepository,
        IConfigDriftRepository driftRepository)
    {
        _dataRepository = dataRepository;
        _alertRepository = alertRepository;
        _driftRepository = driftRepository;
    }

    // ========================================================================
    // MAINTENANCE: Data Management Over Time
    // ========================================================================

    /// <summary>
    /// Gets data age and size breakdown for maintenance planning.
    /// </summary>
    public async Task<DataMaintenanceReport> GetDataMaintenanceReportAsync(
        CancellationToken cancellationToken = default)
    {
        var stats = await _dataRepository.GetDataStatsAsync(cancellationToken);

        var recommendations = new List<MaintenanceRecommendation>();

        // Check for old data
        if (stats.RunHistoryOlderThan90Days > 1000)
        {
            recommendations.Add(new MaintenanceRecommendation(
                Type: MaintenanceType.ArchiveData,
                Priority: RecommendationPriority.Medium,
                Title: "Archive old run history",
                Description: $"{stats.RunHistoryOlderThan90Days:N0} runs older than 90 days can be archived",
                Impact: $"Free up ~{FormatSize(stats.OldDataSize)}",
                Action: "maintenance:archive:runs:90d"));
        }

        // Check for large logs
        if (stats.LogSize > 500 * 1024 * 1024) // 500MB
        {
            recommendations.Add(new MaintenanceRecommendation(
                Type: MaintenanceType.TruncateLogs,
                Priority: RecommendationPriority.High,
                Title: "Truncate verbose logs",
                Description: $"Logs are using {FormatSize(stats.LogSize)}",
                Impact: "Improve app performance",
                Action: "maintenance:truncate:logs"));
        }

        // Check for orphaned data
        if (stats.OrphanedRecords > 0)
        {
            recommendations.Add(new MaintenanceRecommendation(
                Type: MaintenanceType.CleanupOrphans,
                Priority: RecommendationPriority.Low,
                Title: "Clean up orphaned records",
                Description: $"{stats.OrphanedRecords} records no longer linked to active resources",
                Impact: "Reduce database clutter",
                Action: "maintenance:cleanup:orphans"));
        }

        return new DataMaintenanceReport(
            TotalDataSize: stats.TotalSize,
            DataByCategory: stats.SizeByCategory,
            OldDataSize: stats.OldDataSize,
            Recommendations: recommendations,
            LastMaintenanceRun: stats.LastMaintenanceAt,
            HealthScore: CalculateDataHealthScore(stats));
    }

    /// <summary>
    /// Archives old data based on retention policy.
    /// </summary>
    public async Task<ArchiveResult> ArchiveOldDataAsync(
        ArchiveOptions options,
        CancellationToken cancellationToken = default)
    {
        var archived = await _dataRepository.ArchiveDataAsync(options, cancellationToken);

        return new ArchiveResult(
            Success: true,
            ArchivedRecords: archived.RecordCount,
            FreedBytes: archived.FreedSize,
            ArchiveLocation: archived.ArchivePath,
            CanRestore: true,
            RestoreDeadline: DateTimeOffset.UtcNow.AddDays(options.RetentionDays));
    }

    /// <summary>
    /// Gets performance trends over time.
    /// </summary>
    public async Task<PerformanceTrend> GetPerformanceTrendAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var metrics = await _dataRepository.GetPerformanceMetricsAsync(window, cancellationToken);

        var trend = CalculateTrend(metrics);

        return new PerformanceTrend(
            Window: window,
            CurrentAvgResponseTime: metrics.CurrentAvgResponseTime,
            PreviousAvgResponseTime: metrics.PreviousAvgResponseTime,
            Trend: trend,
            TrendDescription: GetTrendDescription(trend, metrics),
            Metrics: metrics.DataPoints,
            Recommendations: GetPerformanceRecommendations(metrics));
    }

    // ========================================================================
    // NOISE: Alert & Feed Tuning
    // ========================================================================

    /// <summary>
    /// Gets alert noise analysis.
    /// </summary>
    public async Task<AlertNoiseAnalysis> AnalyzeAlertNoiseAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var alerts = await _alertRepository.GetAlertHistoryAsync(window, cancellationToken);

        // Find noisy alerts
        var noisyAlerts = alerts
            .GroupBy(a => a.RuleId)
            .Where(g => g.Count() > 10 && g.Count(a => a.WasActedOn) < g.Count() * 0.1)
            .Select(g => new NoisyAlertInfo(
                RuleId: g.Key,
                RuleName: g.First().RuleName,
                TriggerCount: g.Count(),
                ActionRate: (double)g.Count(a => a.WasActedOn) / g.Count(),
                Recommendation: g.Count() > 50
                    ? "Consider disabling or adjusting threshold"
                    : "Review threshold settings"))
            .ToList();

        // Find duplicate alerts
        var duplicates = alerts
            .GroupBy(a => new { a.RuleId, a.ResourceId })
            .Where(g => g.Count() > 5)
            .Count();

        return new AlertNoiseAnalysis(
            TotalAlerts: alerts.Count,
            ActionedAlerts: alerts.Count(a => a.WasActedOn),
            NoisyAlerts: noisyAlerts,
            DuplicateAlertPatterns: duplicates,
            RecommendedActions: GenerateAlertTuningRecommendations(noisyAlerts, alerts),
            NoiseScore: CalculateNoiseScore(alerts),
            Window: window);
    }

    /// <summary>
    /// Gets verbosity settings with recommendations.
    /// </summary>
    public async Task<VerbositySettings> GetVerbositySettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await _alertRepository.GetVerbositySettingsAsync(cancellationToken);

        return new VerbositySettings(
            GlobalLevel: settings.GlobalLevel,
            ByCategory: settings.CategoryLevels,
            ByIntegration: settings.IntegrationLevels,
            Presets: [
                new VerbosityPreset("quiet", "Quiet", "Only critical alerts", VerbosityLevel.Critical),
                new VerbosityPreset("normal", "Normal", "Important alerts and warnings", VerbosityLevel.Warning),
                new VerbosityPreset("verbose", "Verbose", "All alerts including info", VerbosityLevel.Info),
                new VerbosityPreset("debug", "Debug", "Everything including debug info", VerbosityLevel.Debug)
            ],
            CurrentEstimatedAlertsPerDay: settings.EstimatedDailyAlerts);
    }

    /// <summary>
    /// Updates verbosity settings.
    /// </summary>
    public async Task<VerbosityUpdateResult> UpdateVerbosityAsync(
        VerbosityUpdate update,
        CancellationToken cancellationToken = default)
    {
        var previousSettings = await _alertRepository.GetVerbositySettingsAsync(cancellationToken);
        await _alertRepository.UpdateVerbosityAsync(update, cancellationToken);
        var newSettings = await _alertRepository.GetVerbositySettingsAsync(cancellationToken);

        var estimatedChange = newSettings.EstimatedDailyAlerts - previousSettings.EstimatedDailyAlerts;

        return new VerbosityUpdateResult(
            Success: true,
            PreviousLevel: previousSettings.GlobalLevel,
            NewLevel: newSettings.GlobalLevel,
            EstimatedAlertChange: estimatedChange,
            Message: estimatedChange < 0
                ? $"Expected ~{Math.Abs(estimatedChange)} fewer alerts per day"
                : estimatedChange > 0
                    ? $"Expected ~{estimatedChange} more alerts per day"
                    : "No change in expected alert volume");
    }

    // ========================================================================
    // CHANGE: Team Evolution & Config Drift
    // ========================================================================

    /// <summary>
    /// Detects configuration drift from baseline.
    /// </summary>
    public async Task<ConfigDriftReport> DetectConfigDriftAsync(
        string? baselineId = null,
        CancellationToken cancellationToken = default)
    {
        var baseline = baselineId != null
            ? await _driftRepository.GetBaselineAsync(baselineId, cancellationToken)
            : await _driftRepository.GetLatestBaselineAsync(cancellationToken);

        if (baseline == null)
        {
            return new ConfigDriftReport(
                HasBaseline: false,
                Drifts: [],
                DriftScore: 0,
                Summary: "No baseline configuration found. Create a baseline to detect drift.",
                BaselineId: null,
                BaselineCreatedAt: null);
        }

        var currentConfig = await _driftRepository.GetCurrentConfigAsync(cancellationToken);
        var drifts = await _driftRepository.CompareConfigsAsync(baseline.Config, currentConfig, cancellationToken);

        var criticalDrifts = drifts.Where(d => d.Severity == DriftSeverity.Critical).ToList();
        var warnings = drifts.Where(d => d.Severity == DriftSeverity.Warning).ToList();

        if (criticalDrifts.Count > 0)
        {
            OnConfigDriftDetected(criticalDrifts);
        }

        return new ConfigDriftReport(
            HasBaseline: true,
            Drifts: drifts,
            DriftScore: CalculateDriftScore(drifts),
            Summary: GenerateDriftSummary(drifts),
            BaselineId: baseline.Id,
            BaselineCreatedAt: baseline.CreatedAt,
            CriticalDrifts: criticalDrifts,
            Warnings: warnings);
    }

    /// <summary>
    /// Creates a configuration baseline.
    /// </summary>
    public async Task<BaselineResult> CreateBaselineAsync(
        string name,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var currentConfig = await _driftRepository.GetCurrentConfigAsync(cancellationToken);

        var baseline = new ConfigBaseline
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = description,
            Config = currentConfig,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "current_user" // Would come from context
        };

        await _driftRepository.SaveBaselineAsync(baseline, cancellationToken);

        return new BaselineResult(
            Success: true,
            BaselineId: baseline.Id,
            Message: $"Baseline '{name}' created successfully",
            ConfigItems: currentConfig.Count);
    }

    /// <summary>
    /// Gets safe team evolution recommendations.
    /// </summary>
    public async Task<TeamEvolutionReport> GetTeamEvolutionReportAsync(
        CancellationToken cancellationToken = default)
    {
        var recentChanges = await _driftRepository.GetRecentTeamChangesAsync(TimeSpan.FromDays(30), cancellationToken);

        var recommendations = new List<EvolutionRecommendation>();

        // Check for permission creep
        var permissionChanges = recentChanges.Where(c => c.ChangeType == "permission").ToList();
        if (permissionChanges.Count > 10)
        {
            recommendations.Add(new EvolutionRecommendation(
                Type: "review_permissions",
                Title: "Review recent permission changes",
                Description: $"{permissionChanges.Count} permission changes in last 30 days",
                Priority: RecommendationPriority.Medium));
        }

        // Check for inactive users with high privileges
        var inactiveAdmins = await _driftRepository.GetInactiveHighPrivilegeUsersAsync(
            TimeSpan.FromDays(30), cancellationToken);

        if (inactiveAdmins.Count > 0)
        {
            recommendations.Add(new EvolutionRecommendation(
                Type: "review_inactive_admins",
                Title: "Review inactive admin accounts",
                Description: $"{inactiveAdmins.Count} admin accounts inactive for 30+ days",
                Priority: RecommendationPriority.High));
        }

        return new TeamEvolutionReport(
            RecentChanges: recentChanges,
            Recommendations: recommendations,
            TeamHealthScore: CalculateTeamHealthScore(recentChanges, inactiveAdmins),
            InactiveAdminCount: inactiveAdmins.Count);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private static int CalculateDataHealthScore(DataStats stats)
    {
        var score = 100;

        if (stats.OldDataSize > stats.TotalSize * 0.3) score -= 20;
        if (stats.OrphanedRecords > 100) score -= 10;
        if (stats.LogSize > 500 * 1024 * 1024) score -= 15;
        if (stats.LastMaintenanceAt < DateTimeOffset.UtcNow.AddDays(-30)) score -= 10;

        return Math.Max(0, score);
    }

    private static TrendDirection CalculateTrend(PerformanceMetrics metrics)
    {
        var change = metrics.CurrentAvgResponseTime - metrics.PreviousAvgResponseTime;
        var percentChange = metrics.PreviousAvgResponseTime > 0
            ? change / metrics.PreviousAvgResponseTime
            : 0;

        return percentChange switch
        {
            > 0.1 => TrendDirection.Degrading,
            < -0.1 => TrendDirection.Improving,
            _ => TrendDirection.Stable
        };
    }

    private static string GetTrendDescription(TrendDirection trend, PerformanceMetrics metrics)
    {
        return trend switch
        {
            TrendDirection.Improving => $"Performance improved by {(metrics.PreviousAvgResponseTime - metrics.CurrentAvgResponseTime) / metrics.PreviousAvgResponseTime:P0}",
            TrendDirection.Degrading => $"Performance degraded by {(metrics.CurrentAvgResponseTime - metrics.PreviousAvgResponseTime) / metrics.PreviousAvgResponseTime:P0}",
            _ => "Performance is stable"
        };
    }

    private static IReadOnlyList<string> GetPerformanceRecommendations(PerformanceMetrics metrics)
    {
        var recommendations = new List<string>();

        if (metrics.CurrentAvgResponseTime > 1000)
            recommendations.Add("Consider archiving old data to improve query performance");

        if (metrics.DatabaseSizeGrowthRate > 0.1)
            recommendations.Add("Database is growing quickly - review retention policies");

        return recommendations;
    }

    private static IReadOnlyList<AlertTuningRecommendation> GenerateAlertTuningRecommendations(
        IReadOnlyList<NoisyAlertInfo> noisyAlerts,
        IReadOnlyList<AlertRecord> allAlerts)
    {
        var recommendations = new List<AlertTuningRecommendation>();

        foreach (var noisy in noisyAlerts.Take(5))
        {
            recommendations.Add(new AlertTuningRecommendation(
                RuleId: noisy.RuleId,
                Recommendation: noisy.TriggerCount > 50 ? "Disable or adjust threshold" : "Increase threshold",
                ExpectedImpact: $"Reduce alerts by ~{noisy.TriggerCount * 0.8:N0} per period"));
        }

        return recommendations;
    }

    private static int CalculateNoiseScore(IReadOnlyList<AlertRecord> alerts)
    {
        if (alerts.Count == 0) return 100;

        var actionRate = (double)alerts.Count(a => a.WasActedOn) / alerts.Count;
        return (int)(actionRate * 100);
    }

    private static int CalculateDriftScore(IReadOnlyList<ConfigDrift> drifts)
    {
        var score = 100;
        score -= drifts.Count(d => d.Severity == DriftSeverity.Critical) * 20;
        score -= drifts.Count(d => d.Severity == DriftSeverity.Warning) * 5;
        score -= drifts.Count(d => d.Severity == DriftSeverity.Info) * 1;
        return Math.Max(0, score);
    }

    private static string GenerateDriftSummary(IReadOnlyList<ConfigDrift> drifts)
    {
        if (drifts.Count == 0) return "No configuration drift detected";

        var critical = drifts.Count(d => d.Severity == DriftSeverity.Critical);
        var warnings = drifts.Count(d => d.Severity == DriftSeverity.Warning);

        if (critical > 0) return $"{critical} critical drift(s) detected - review immediately";
        if (warnings > 0) return $"{warnings} configuration warning(s) - review recommended";
        return $"{drifts.Count} minor configuration change(s) since baseline";
    }

    private static int CalculateTeamHealthScore(
        IReadOnlyList<TeamChange> changes,
        IReadOnlyList<InactiveUser> inactiveAdmins)
    {
        var score = 100;
        if (inactiveAdmins.Count > 0) score -= inactiveAdmins.Count * 10;
        if (changes.Count(c => c.ChangeType == "permission") > 20) score -= 15;
        return Math.Max(0, Math.Min(100, score));
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var index = 0;
        double value = bytes;
        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:F1} {suffixes[index]}";
    }

    private void OnConfigDriftDetected(IReadOnlyList<ConfigDrift> criticalDrifts)
    {
        ConfigDriftDetected?.Invoke(this, new ConfigDriftDetectedEventArgs(criticalDrifts));
    }
}

// ============================================================================
// Day-2 Experience Types
// ============================================================================

public sealed record DataMaintenanceReport(
    long TotalDataSize,
    IReadOnlyDictionary<string, long> DataByCategory,
    long OldDataSize,
    IReadOnlyList<MaintenanceRecommendation> Recommendations,
    DateTimeOffset? LastMaintenanceRun,
    int HealthScore);

public sealed record MaintenanceRecommendation(
    MaintenanceType Type,
    RecommendationPriority Priority,
    string Title,
    string Description,
    string Impact,
    string Action);

public enum MaintenanceType { ArchiveData, TruncateLogs, CleanupOrphans, OptimizeDatabase }

public enum RecommendationPriority { Low, Medium, High, Critical }

public sealed record ArchiveOptions(
    int OlderThanDays,
    IReadOnlyList<string>? DataTypes = null,
    int RetentionDays = 365);

public sealed record ArchiveResult(
    bool Success,
    int ArchivedRecords,
    long FreedBytes,
    string ArchiveLocation,
    bool CanRestore,
    DateTimeOffset RestoreDeadline);

public sealed record PerformanceTrend(
    TimeSpan Window,
    double CurrentAvgResponseTime,
    double PreviousAvgResponseTime,
    TrendDirection Trend,
    string TrendDescription,
    IReadOnlyList<PerformanceDataPoint> Metrics,
    IReadOnlyList<string> Recommendations);

public enum TrendDirection { Improving, Stable, Degrading }

public sealed record PerformanceDataPoint(DateTimeOffset Timestamp, double Value);

public sealed record AlertNoiseAnalysis(
    int TotalAlerts,
    int ActionedAlerts,
    IReadOnlyList<NoisyAlertInfo> NoisyAlerts,
    int DuplicateAlertPatterns,
    IReadOnlyList<AlertTuningRecommendation> RecommendedActions,
    int NoiseScore,
    TimeSpan Window);

public sealed record NoisyAlertInfo(
    string RuleId,
    string RuleName,
    int TriggerCount,
    double ActionRate,
    string Recommendation);

public sealed record AlertTuningRecommendation(
    string RuleId,
    string Recommendation,
    string ExpectedImpact);

public sealed record VerbositySettings(
    VerbosityLevel GlobalLevel,
    IReadOnlyDictionary<string, VerbosityLevel> ByCategory,
    IReadOnlyDictionary<string, VerbosityLevel> ByIntegration,
    IReadOnlyList<VerbosityPreset> Presets,
    int CurrentEstimatedAlertsPerDay);

public enum VerbosityLevel { Critical, Warning, Info, Debug }

public sealed record VerbosityPreset(string Id, string Name, string Description, VerbosityLevel Level);

public sealed record VerbosityUpdate(
    VerbosityLevel? GlobalLevel = null,
    Dictionary<string, VerbosityLevel>? CategoryLevels = null,
    Dictionary<string, VerbosityLevel>? IntegrationLevels = null);

public sealed record VerbosityUpdateResult(
    bool Success,
    VerbosityLevel PreviousLevel,
    VerbosityLevel NewLevel,
    int EstimatedAlertChange,
    string Message);

public sealed record ConfigDriftReport(
    bool HasBaseline,
    IReadOnlyList<ConfigDrift> Drifts,
    int DriftScore,
    string Summary,
    string? BaselineId,
    DateTimeOffset? BaselineCreatedAt,
    IReadOnlyList<ConfigDrift>? CriticalDrifts = null,
    IReadOnlyList<ConfigDrift>? Warnings = null);

public sealed record ConfigDrift(
    string ConfigPath,
    string Description,
    object? BaselineValue,
    object? CurrentValue,
    DriftSeverity Severity,
    DateTimeOffset DetectedAt);

public enum DriftSeverity { Info, Warning, Critical }

public sealed record BaselineResult(
    bool Success,
    string BaselineId,
    string Message,
    int ConfigItems);

public sealed record TeamEvolutionReport(
    IReadOnlyList<TeamChange> RecentChanges,
    IReadOnlyList<EvolutionRecommendation> Recommendations,
    int TeamHealthScore,
    int InactiveAdminCount);

public sealed record TeamChange(
    string ChangeType,
    string Description,
    DateTimeOffset Timestamp,
    string ChangedBy);

public sealed record EvolutionRecommendation(
    string Type,
    string Title,
    string Description,
    RecommendationPriority Priority);

// Internal types
public sealed class DataStats
{
    public long TotalSize { get; set; }
    public Dictionary<string, long> SizeByCategory { get; set; } = new();
    public long OldDataSize { get; set; }
    public int RunHistoryOlderThan90Days { get; set; }
    public long LogSize { get; set; }
    public int OrphanedRecords { get; set; }
    public DateTimeOffset? LastMaintenanceAt { get; set; }
}

public sealed class PerformanceMetrics
{
    public double CurrentAvgResponseTime { get; set; }
    public double PreviousAvgResponseTime { get; set; }
    public double DatabaseSizeGrowthRate { get; set; }
    public List<PerformanceDataPoint> DataPoints { get; set; } = new();
}

public sealed class AlertRecord
{
    public required string RuleId { get; set; }
    public required string RuleName { get; set; }
    public required string ResourceId { get; set; }
    public bool WasActedOn { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public sealed class VerbositySettingsData
{
    public VerbosityLevel GlobalLevel { get; set; }
    public Dictionary<string, VerbosityLevel> CategoryLevels { get; set; } = new();
    public Dictionary<string, VerbosityLevel> IntegrationLevels { get; set; } = new();
    public int EstimatedDailyAlerts { get; set; }
}

public sealed class ConfigBaseline
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required Dictionary<string, object> Config { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}

public sealed class InactiveUser
{
    public required string UserId { get; set; }
    public required string UserName { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
}

public sealed class ArchiveResultData
{
    public int RecordCount { get; set; }
    public long FreedSize { get; set; }
    public required string ArchivePath { get; set; }
}

// Events
public sealed class MaintenanceRecommendationEventArgs : EventArgs
{
    public MaintenanceRecommendation Recommendation { get; }
    public MaintenanceRecommendationEventArgs(MaintenanceRecommendation recommendation) => Recommendation = recommendation;
}

public sealed class ConfigDriftDetectedEventArgs : EventArgs
{
    public IReadOnlyList<ConfigDrift> CriticalDrifts { get; }
    public ConfigDriftDetectedEventArgs(IReadOnlyList<ConfigDrift> drifts) => CriticalDrifts = drifts;
}

// Interfaces
public interface IDataMaintenanceRepository
{
    Task<DataStats> GetDataStatsAsync(CancellationToken cancellationToken);
    Task<ArchiveResultData> ArchiveDataAsync(ArchiveOptions options, CancellationToken cancellationToken);
    Task<PerformanceMetrics> GetPerformanceMetricsAsync(TimeSpan window, CancellationToken cancellationToken);
}

public interface IAlertTuningRepository
{
    Task<IReadOnlyList<AlertRecord>> GetAlertHistoryAsync(TimeSpan window, CancellationToken cancellationToken);
    Task<VerbositySettingsData> GetVerbositySettingsAsync(CancellationToken cancellationToken);
    Task UpdateVerbosityAsync(VerbosityUpdate update, CancellationToken cancellationToken);
}

public interface IConfigDriftRepository
{
    Task<ConfigBaseline?> GetBaselineAsync(string baselineId, CancellationToken cancellationToken);
    Task<ConfigBaseline?> GetLatestBaselineAsync(CancellationToken cancellationToken);
    Task<Dictionary<string, object>> GetCurrentConfigAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ConfigDrift>> CompareConfigsAsync(Dictionary<string, object> baseline, Dictionary<string, object> current, CancellationToken cancellationToken);
    Task SaveBaselineAsync(ConfigBaseline baseline, CancellationToken cancellationToken);
    Task<IReadOnlyList<TeamChange>> GetRecentTeamChangesAsync(TimeSpan window, CancellationToken cancellationToken);
    Task<IReadOnlyList<InactiveUser>> GetInactiveHighPrivilegeUsersAsync(TimeSpan inactiveWindow, CancellationToken cancellationToken);
}
