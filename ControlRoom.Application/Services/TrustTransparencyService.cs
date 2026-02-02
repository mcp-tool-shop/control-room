namespace ControlRoom.Application.Services;

/// <summary>
/// Trust & Transparency: Ensures users understand where their data goes,
/// how AI is used, and that nothing happens silently.
///
/// Checklist items addressed:
/// - Local vs cloud behavior explicit
/// - Data residency clear
/// - Explain AI usage
/// - Opt-in where required
/// - Nothing happens silently
/// - Audit logs available
/// </summary>
public sealed class TrustTransparencyService
{
    private readonly IDataResidencyRepository _residencyRepository;
    private readonly IAIUsageRepository _aiUsageRepository;
    private readonly IUserPreferencesRepository _preferencesRepository;
    private readonly IAuditLogRepository _auditRepository;

    public event EventHandler<DataFlowEventArgs>? DataFlowOccurred;
    public event EventHandler<AIUsageEventArgs>? AIUsageOccurred;

    public TrustTransparencyService(
        IDataResidencyRepository residencyRepository,
        IAIUsageRepository aiUsageRepository,
        IUserPreferencesRepository preferencesRepository,
        IAuditLogRepository auditRepository)
    {
        _residencyRepository = residencyRepository;
        _aiUsageRepository = aiUsageRepository;
        _preferencesRepository = preferencesRepository;
        _auditRepository = auditRepository;
    }

    // ========================================================================
    // DATA: Location & Residency Transparency
    // ========================================================================

    /// <summary>
    /// Gets a clear breakdown of where data lives.
    /// </summary>
    public async Task<DataResidencyReport> GetDataResidencyReportAsync(
        CancellationToken cancellationToken = default)
    {
        var localData = await _residencyRepository.GetLocalDataSummaryAsync(cancellationToken);
        var cloudData = await _residencyRepository.GetCloudDataSummaryAsync(cancellationToken);
        var syncStatus = await _residencyRepository.GetSyncStatusAsync(cancellationToken);

        return new DataResidencyReport(
            LocalStorage: new DataLocationInfo(
                Location: "Local Device",
                Description: "Stored on your machine, never leaves your device",
                Icon: "\uE8B7",
                Items: [
                    new DataItem("Configuration", "App settings and preferences", localData.ConfigSize, true),
                    new DataItem("Cached Data", "Temporary data for offline use", localData.CacheSize, true),
                    new DataItem("Credentials", "Encrypted secrets and tokens", localData.CredentialSize, true),
                    new DataItem("Run History", "Local execution logs", localData.HistorySize, localData.SyncsHistory),
                    new DataItem("Runbook Drafts", "Unsaved changes", localData.DraftSize, false)
                ]),
            CloudStorage: cloudData.IsConfigured ? new DataLocationInfo(
                Location: GetCloudLocationDescription(cloudData.Region),
                Description: cloudData.IsConfigured
                    ? $"Stored in {cloudData.ProviderName} ({cloudData.Region})"
                    : "No cloud storage configured",
                Icon: "\uE753",
                Items: [
                    new DataItem("Team Data", "Shared runbooks and configurations", cloudData.TeamDataSize, true),
                    new DataItem("Execution History", "Synced run history", cloudData.HistorySize, true),
                    new DataItem("Integrations", "Connection metadata (no credentials)", cloudData.IntegrationSize, true)
                ]) : null,
            SyncBehavior: new SyncBehaviorInfo(
                IsEnabled: syncStatus.IsEnabled,
                Direction: syncStatus.Direction,
                LastSyncAt: syncStatus.LastSyncAt,
                PendingChanges: syncStatus.PendingChanges,
                ExplicitOptIn: syncStatus.UserOptedIn),
            Summary: GenerateResidencySummary(localData, cloudData));
    }

    /// <summary>
    /// Gets where a specific piece of data is stored.
    /// </summary>
    public async Task<DataLocationDetail> GetDataLocationAsync(
        string dataType,
        string dataId,
        CancellationToken cancellationToken = default)
    {
        var location = await _residencyRepository.GetDataLocationAsync(dataType, dataId, cancellationToken);

        return new DataLocationDetail(
            DataType: dataType,
            DataId: dataId,
            PrimaryLocation: location.PrimaryLocation,
            ReplicatedTo: location.ReplicatedTo,
            EncryptedAtRest: location.IsEncrypted,
            EncryptionMethod: location.EncryptionMethod,
            RetentionPolicy: location.RetentionPolicy,
            CanDelete: location.CanDelete,
            DeleteImpact: location.DeleteImpact);
    }

    /// <summary>
    /// Gets notifications about data flow events.
    /// </summary>
    public void NotifyDataFlow(DataFlowEvent flowEvent)
    {
        DataFlowOccurred?.Invoke(this, new DataFlowEventArgs(flowEvent));
    }

    // ========================================================================
    // AI: Usage Transparency & Consent
    // ========================================================================

    /// <summary>
    /// Gets a clear explanation of how AI is used.
    /// </summary>
    public AIUsagePolicy GetAIUsagePolicy()
    {
        return new AIUsagePolicy(
            OverallStatement: "Control Room uses AI to help you troubleshoot issues and optimize your workflows. " +
                             "AI features are optional and can be disabled at any time.",
            Features: [
                new AIFeatureInfo(
                    Id: "error_analysis",
                    Name: "Error Analysis",
                    Description: "Analyzes error logs to suggest root causes and fixes",
                    DataUsed: "Error messages and stack traces from failed runs",
                    IsOptional: true,
                    IsEnabledByDefault: true,
                    RequiresExplicitConsent: false,
                    ProcessingLocation: "Local (Ollama) or Cloud (if configured)"),

                new AIFeatureInfo(
                    Id: "smart_suggestions",
                    Name: "Smart Suggestions",
                    Description: "Suggests optimizations for runbooks and schedules",
                    DataUsed: "Runbook configurations and execution patterns",
                    IsOptional: true,
                    IsEnabledByDefault: true,
                    RequiresExplicitConsent: false,
                    ProcessingLocation: "Local only"),

                new AIFeatureInfo(
                    Id: "natural_language",
                    Name: "Natural Language Commands",
                    Description: "Allows you to describe tasks in plain English",
                    DataUsed: "Your typed commands only",
                    IsOptional: true,
                    IsEnabledByDefault: false,
                    RequiresExplicitConsent: true,
                    ProcessingLocation: "Cloud API (requires opt-in)"),

                new AIFeatureInfo(
                    Id: "usage_analytics",
                    Name: "Usage Analytics",
                    Description: "Aggregated usage patterns to improve product",
                    DataUsed: "Anonymous feature usage counts",
                    IsOptional: true,
                    IsEnabledByDefault: false,
                    RequiresExplicitConsent: true,
                    ProcessingLocation: "Cloud (anonymized)")
            ],
            PrivacyCommitments: [
                "We never train AI models on your data without explicit consent",
                "Local AI processing is always available as an alternative",
                "You can export and delete all your data at any time",
                "AI features can be completely disabled"
            ]);
    }

    /// <summary>
    /// Gets the user's AI consent status.
    /// </summary>
    public async Task<AIConsentStatus> GetAIConsentStatusAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var preferences = await _preferencesRepository.GetAIPreferencesAsync(userId, cancellationToken);
        var policy = GetAIUsagePolicy();

        var featureStatuses = policy.Features.Select(f => new AIFeatureConsentStatus(
            FeatureId: f.Id,
            FeatureName: f.Name,
            RequiresConsent: f.RequiresExplicitConsent,
            HasConsented: preferences.ConsentedFeatures.Contains(f.Id),
            IsEnabled: preferences.EnabledFeatures.Contains(f.Id),
            ConsentedAt: preferences.ConsentTimestamps.GetValueOrDefault(f.Id)
        )).ToList();

        return new AIConsentStatus(
            UserId: userId,
            FeatureStatuses: featureStatuses,
            GlobalAIEnabled: preferences.GlobalAIEnabled,
            PreferLocalProcessing: preferences.PreferLocalProcessing,
            LastUpdated: preferences.LastUpdated);
    }

    /// <summary>
    /// Updates AI consent for a feature.
    /// </summary>
    public async Task<ConsentUpdateResult> UpdateAIConsentAsync(
        string userId,
        string featureId,
        bool consent,
        CancellationToken cancellationToken = default)
    {
        var policy = GetAIUsagePolicy();
        var feature = policy.Features.FirstOrDefault(f => f.Id == featureId);

        if (feature == null)
        {
            return new ConsentUpdateResult(
                Success: false,
                Message: $"Unknown AI feature: {featureId}");
        }

        await _preferencesRepository.UpdateAIConsentAsync(
            userId, featureId, consent, cancellationToken);

        // Audit the consent change
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = consent ? AuditAction.PermissionGranted : AuditAction.PermissionRevoked,
            ActorId = userId,
            ResourceId = featureId,
            ResourceType = "AIFeature",
            Details = new Dictionary<string, object>
            {
                ["feature"] = feature.Name,
                ["consent"] = consent,
                ["dataUsed"] = feature.DataUsed
            }
        }, cancellationToken);

        return new ConsentUpdateResult(
            Success: true,
            Message: consent
                ? $"Enabled {feature.Name}"
                : $"Disabled {feature.Name}. Your data will no longer be used for this feature.");
    }

    /// <summary>
    /// Records that AI was used, for transparency.
    /// </summary>
    public async Task RecordAIUsageAsync(
        AIUsageRecord record,
        CancellationToken cancellationToken = default)
    {
        await _aiUsageRepository.RecordUsageAsync(record, cancellationToken);

        AIUsageOccurred?.Invoke(this, new AIUsageEventArgs(record));
    }

    /// <summary>
    /// Gets AI usage history for transparency.
    /// </summary>
    public async Task<AIUsageHistory> GetAIUsageHistoryAsync(
        string userId,
        TimeSpan? window = null,
        CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.Subtract(window ?? TimeSpan.FromDays(30));
        var records = await _aiUsageRepository.GetUsageHistoryAsync(userId, since, cancellationToken);

        var byFeature = records.GroupBy(r => r.FeatureId)
            .ToDictionary(g => g.Key, g => g.Count());

        return new AIUsageHistory(
            UserId: userId,
            Records: records,
            UsageByFeature: byFeature,
            TotalUsageCount: records.Count,
            Since: since);
    }

    // ========================================================================
    // CONTROL: Nothing Happens Silently
    // ========================================================================

    /// <summary>
    /// Gets all background activities currently happening.
    /// </summary>
    public async Task<BackgroundActivityReport> GetBackgroundActivitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var activities = await _residencyRepository.GetActiveBackgroundTasksAsync(cancellationToken);

        return new BackgroundActivityReport(
            Activities: activities.Select(a => new BackgroundActivityInfo(
                Id: a.Id,
                Type: a.Type,
                Description: a.Description,
                StartedAt: a.StartedAt,
                Progress: a.Progress,
                CanCancel: a.CanCancel,
                DataInvolved: a.DataDescription)).ToList(),
            HasActiveSync: activities.Any(a => a.Type == "sync"),
            HasActiveAI: activities.Any(a => a.Type == "ai_processing"),
            HasActiveNetwork: activities.Any(a => a.Type == "network"),
            Summary: activities.Count == 0
                ? "No background activities"
                : $"{activities.Count} active: {string.Join(", ", activities.Select(a => a.Type).Distinct())}");
    }

    /// <summary>
    /// Gets notifications about significant events.
    /// </summary>
    public async Task<IReadOnlyList<TransparencyNotification>> GetRecentNotificationsAsync(
        string userId,
        int maxCount = 20,
        CancellationToken cancellationToken = default)
    {
        return await _residencyRepository.GetTransparencyNotificationsAsync(userId, maxCount, cancellationToken);
    }

    /// <summary>
    /// Creates a transparency notification for a significant event.
    /// </summary>
    public async Task NotifyUserAsync(
        string userId,
        TransparencyNotification notification,
        CancellationToken cancellationToken = default)
    {
        await _residencyRepository.SaveNotificationAsync(userId, notification, cancellationToken);
    }

    // ========================================================================
    // AUDIT: Full Audit Log Access
    // ========================================================================

    /// <summary>
    /// Gets audit logs with filtering.
    /// </summary>
    public async Task<AuditLogResult> GetAuditLogsAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default)
    {
        var entries = await _auditRepository.QueryAsync(new AuditHistoryQuery
        {
            ActorId = query.ActorId,
            ResourceId = query.ResourceId,
            Actions = query.Actions,
            Since = query.Since,
            Until = query.Until,
            MaxResults = query.MaxResults,
            Offset = query.Offset
        }, cancellationToken);

        var totalCount = await _auditRepository.CountAsync(new AuditHistoryQuery
        {
            ActorId = query.ActorId,
            ResourceId = query.ResourceId,
            Actions = query.Actions,
            Since = query.Since,
            Until = query.Until
        }, cancellationToken);

        return new AuditLogResult(
            Entries: entries.Select(e => new AuditLogEntry(
                Id: e.Id,
                Action: e.Action.ToString(),
                Actor: e.ActorName ?? e.ActorId,
                Timestamp: e.Timestamp,
                ResourceType: e.ResourceType,
                ResourceId: e.ResourceId,
                Details: e.Details,
                Summary: FormatAuditSummary(e))).ToList(),
            TotalCount: totalCount,
            HasMore: entries.Count + query.Offset < totalCount);
    }

    /// <summary>
    /// Exports audit logs for compliance.
    /// </summary>
    public async Task<AuditExport> ExportAuditLogsAsync(
        AuditLogQuery query,
        ExportFormat format,
        CancellationToken cancellationToken = default)
    {
        var logs = await GetAuditLogsAsync(query with { MaxResults = 10000 }, cancellationToken);

        var content = format switch
        {
            ExportFormat.JSON => System.Text.Json.JsonSerializer.Serialize(logs.Entries),
            ExportFormat.CSV => GenerateCsvExport(logs.Entries),
            _ => throw new NotSupportedException($"Format {format} not supported")
        };

        return new AuditExport(
            Content: content,
            Format: format,
            EntryCount: logs.Entries.Count,
            GeneratedAt: DateTimeOffset.UtcNow,
            Query: query);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private static string GetCloudLocationDescription(string region)
    {
        return region switch
        {
            "us-east-1" => "US East (Virginia)",
            "us-west-2" => "US West (Oregon)",
            "eu-west-1" => "Europe (Ireland)",
            "eu-central-1" => "Europe (Frankfurt)",
            "ap-southeast-1" => "Asia Pacific (Singapore)",
            _ => region
        };
    }

    private static string GenerateResidencySummary(LocalDataSummary local, CloudDataSummary cloud)
    {
        if (!cloud.IsConfigured)
        {
            return "All your data is stored locally on this device.";
        }

        return $"Your data is stored locally ({FormatSize(local.TotalSize)}) " +
               $"and synced to {cloud.ProviderName} ({FormatSize(cloud.TotalSize)}).";
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

    private static string FormatAuditSummary(AuditEntry entry)
    {
        return $"{entry.ActorName ?? "User"} {entry.Action.ToString().ToLowerInvariant()} " +
               $"{entry.ResourceType?.ToLowerInvariant() ?? "resource"}";
    }

    private static string GenerateCsvExport(IReadOnlyList<AuditLogEntry> entries)
    {
        var lines = new List<string>
        {
            "Timestamp,Action,Actor,ResourceType,ResourceId,Summary"
        };

        foreach (var entry in entries)
        {
            lines.Add($"\"{entry.Timestamp:O}\",\"{entry.Action}\",\"{entry.Actor}\"," +
                     $"\"{entry.ResourceType}\",\"{entry.ResourceId}\",\"{entry.Summary}\"");
        }

        return string.Join("\n", lines);
    }
}

// ============================================================================
// Trust & Transparency Types
// ============================================================================

/// <summary>
/// Data residency report.
/// </summary>
public sealed record DataResidencyReport(
    DataLocationInfo LocalStorage,
    DataLocationInfo? CloudStorage,
    SyncBehaviorInfo SyncBehavior,
    string Summary);

/// <summary>
/// Data location info.
/// </summary>
public sealed record DataLocationInfo(
    string Location,
    string Description,
    string Icon,
    IReadOnlyList<DataItem> Items);

/// <summary>
/// Data item.
/// </summary>
public sealed record DataItem(
    string Name,
    string Description,
    long SizeBytes,
    bool IsSynced);

/// <summary>
/// Sync behavior info.
/// </summary>
public sealed record SyncBehaviorInfo(
    bool IsEnabled,
    SyncDirection Direction,
    DateTimeOffset? LastSyncAt,
    int PendingChanges,
    bool ExplicitOptIn);

/// <summary>
/// Sync direction.
/// </summary>
public enum SyncDirection
{
    None,
    LocalToCloud,
    CloudToLocal,
    Bidirectional
}

/// <summary>
/// Data location detail.
/// </summary>
public sealed record DataLocationDetail(
    string DataType,
    string DataId,
    string PrimaryLocation,
    IReadOnlyList<string> ReplicatedTo,
    bool EncryptedAtRest,
    string? EncryptionMethod,
    string? RetentionPolicy,
    bool CanDelete,
    string? DeleteImpact);

/// <summary>
/// Local data summary.
/// </summary>
public sealed class LocalDataSummary
{
    public long ConfigSize { get; set; }
    public long CacheSize { get; set; }
    public long CredentialSize { get; set; }
    public long HistorySize { get; set; }
    public long DraftSize { get; set; }
    public bool SyncsHistory { get; set; }
    public long TotalSize => ConfigSize + CacheSize + CredentialSize + HistorySize + DraftSize;
}

/// <summary>
/// Cloud data summary.
/// </summary>
public sealed class CloudDataSummary
{
    public bool IsConfigured { get; set; }
    public string ProviderName { get; set; } = "";
    public string Region { get; set; } = "";
    public long TeamDataSize { get; set; }
    public long HistorySize { get; set; }
    public long IntegrationSize { get; set; }
    public long TotalSize => TeamDataSize + HistorySize + IntegrationSize;
}

/// <summary>
/// Sync status.
/// </summary>
public sealed class SyncStatus
{
    public bool IsEnabled { get; set; }
    public SyncDirection Direction { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public int PendingChanges { get; set; }
    public bool UserOptedIn { get; set; }
}

/// <summary>
/// Data location result.
/// </summary>
public sealed class DataLocationResult
{
    public required string PrimaryLocation { get; set; }
    public IReadOnlyList<string> ReplicatedTo { get; set; } = [];
    public bool IsEncrypted { get; set; }
    public string? EncryptionMethod { get; set; }
    public string? RetentionPolicy { get; set; }
    public bool CanDelete { get; set; }
    public string? DeleteImpact { get; set; }
}

/// <summary>
/// AI usage policy.
/// </summary>
public sealed record AIUsagePolicy(
    string OverallStatement,
    IReadOnlyList<AIFeatureInfo> Features,
    IReadOnlyList<string> PrivacyCommitments);

/// <summary>
/// AI feature info.
/// </summary>
public sealed record AIFeatureInfo(
    string Id,
    string Name,
    string Description,
    string DataUsed,
    bool IsOptional,
    bool IsEnabledByDefault,
    bool RequiresExplicitConsent,
    string ProcessingLocation);

/// <summary>
/// AI consent status.
/// </summary>
public sealed record AIConsentStatus(
    string UserId,
    IReadOnlyList<AIFeatureConsentStatus> FeatureStatuses,
    bool GlobalAIEnabled,
    bool PreferLocalProcessing,
    DateTimeOffset? LastUpdated);

/// <summary>
/// AI feature consent status.
/// </summary>
public sealed record AIFeatureConsentStatus(
    string FeatureId,
    string FeatureName,
    bool RequiresConsent,
    bool HasConsented,
    bool IsEnabled,
    DateTimeOffset? ConsentedAt);

/// <summary>
/// AI preferences.
/// </summary>
public sealed class AIPreferences
{
    public bool GlobalAIEnabled { get; set; } = true;
    public bool PreferLocalProcessing { get; set; } = true;
    public HashSet<string> ConsentedFeatures { get; set; } = [];
    public HashSet<string> EnabledFeatures { get; set; } = [];
    public Dictionary<string, DateTimeOffset> ConsentTimestamps { get; set; } = [];
    public DateTimeOffset? LastUpdated { get; set; }
}

/// <summary>
/// Consent update result.
/// </summary>
public sealed record ConsentUpdateResult(
    bool Success,
    string Message);

/// <summary>
/// AI usage record.
/// </summary>
public sealed record AIUsageRecord(
    string Id,
    string UserId,
    string FeatureId,
    string FeatureName,
    DateTimeOffset Timestamp,
    string? InputSummary,
    string? OutputSummary,
    bool ProcessedLocally);

/// <summary>
/// AI usage history.
/// </summary>
public sealed record AIUsageHistory(
    string UserId,
    IReadOnlyList<AIUsageRecord> Records,
    IReadOnlyDictionary<string, int> UsageByFeature,
    int TotalUsageCount,
    DateTimeOffset Since);

/// <summary>
/// Background activity report.
/// </summary>
public sealed record BackgroundActivityReport(
    IReadOnlyList<BackgroundActivityInfo> Activities,
    bool HasActiveSync,
    bool HasActiveAI,
    bool HasActiveNetwork,
    string Summary);

/// <summary>
/// Background activity info.
/// </summary>
public sealed record BackgroundActivityInfo(
    string Id,
    string Type,
    string Description,
    DateTimeOffset StartedAt,
    double? Progress,
    bool CanCancel,
    string DataInvolved);

/// <summary>
/// Background task.
/// </summary>
public sealed class BackgroundTask
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public required string Description { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public double? Progress { get; set; }
    public bool CanCancel { get; set; }
    public required string DataDescription { get; set; }
}

/// <summary>
/// Transparency notification.
/// </summary>
public sealed record TransparencyNotification(
    string Id,
    string Type,
    string Title,
    string Message,
    DateTimeOffset Timestamp,
    NotificationSeverity Severity,
    string? ActionUrl);

/// <summary>
/// Notification severity.
/// </summary>
public enum NotificationSeverity
{
    Info,
    Warning,
    Important
}

/// <summary>
/// Audit log query.
/// </summary>
public sealed record AuditLogQuery(
    string? ActorId = null,
    string? ResourceId = null,
    IReadOnlyList<AuditAction>? Actions = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    int MaxResults = 100,
    int Offset = 0);

/// <summary>
/// Audit log result.
/// </summary>
public sealed record AuditLogResult(
    IReadOnlyList<AuditLogEntry> Entries,
    int TotalCount,
    bool HasMore);

/// <summary>
/// Audit log entry.
/// </summary>
public sealed record AuditLogEntry(
    string Id,
    string Action,
    string Actor,
    DateTimeOffset Timestamp,
    string? ResourceType,
    string? ResourceId,
    Dictionary<string, object>? Details,
    string Summary);

/// <summary>
/// Export format.
/// </summary>
public enum ExportFormat
{
    JSON,
    CSV
}

/// <summary>
/// Audit export.
/// </summary>
public sealed record AuditExport(
    string Content,
    ExportFormat Format,
    int EntryCount,
    DateTimeOffset GeneratedAt,
    AuditLogQuery Query);

// ============================================================================
// Events
// ============================================================================

public sealed class DataFlowEventArgs : EventArgs
{
    public DataFlowEvent Event { get; }
    public DataFlowEventArgs(DataFlowEvent flowEvent) => Event = flowEvent;
}

public sealed record DataFlowEvent(
    string DataType,
    string Direction,
    string Source,
    string Destination,
    DateTimeOffset Timestamp);

public sealed class AIUsageEventArgs : EventArgs
{
    public AIUsageRecord Record { get; }
    public AIUsageEventArgs(AIUsageRecord record) => Record = record;
}

// ============================================================================
// Interfaces
// ============================================================================

public interface IDataResidencyRepository
{
    Task<LocalDataSummary> GetLocalDataSummaryAsync(CancellationToken cancellationToken);
    Task<CloudDataSummary> GetCloudDataSummaryAsync(CancellationToken cancellationToken);
    Task<SyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken);
    Task<DataLocationResult> GetDataLocationAsync(string dataType, string dataId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackgroundTask>> GetActiveBackgroundTasksAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TransparencyNotification>> GetTransparencyNotificationsAsync(string userId, int maxCount, CancellationToken cancellationToken);
    Task SaveNotificationAsync(string userId, TransparencyNotification notification, CancellationToken cancellationToken);
}

public interface IAIUsageRepository
{
    Task RecordUsageAsync(AIUsageRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<AIUsageRecord>> GetUsageHistoryAsync(string userId, DateTimeOffset since, CancellationToken cancellationToken);
}

public interface IUserPreferencesRepository
{
    Task<AIPreferences> GetAIPreferencesAsync(string userId, CancellationToken cancellationToken);
    Task UpdateAIConsentAsync(string userId, string featureId, bool consent, CancellationToken cancellationToken);
}
