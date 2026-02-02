namespace ControlRoom.Domain.Model;

// ============================================================================
// Integration Hub Domain Model
// ============================================================================

/// <summary>
/// Unique identifier for an integration.
/// </summary>
public readonly record struct IntegrationId(Guid Value)
{
    public static IntegrationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
    public static IntegrationId Parse(string s) => new(Guid.Parse(s));
}

/// <summary>
/// Unique identifier for an integration instance (connected account).
/// </summary>
public readonly record struct IntegrationInstanceId(Guid Value)
{
    public static IntegrationInstanceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
    public static IntegrationInstanceId Parse(string s) => new(Guid.Parse(s));
}

/// <summary>
/// Unique identifier for a webhook endpoint.
/// </summary>
public readonly record struct WebhookId(Guid Value)
{
    public static WebhookId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
    public static WebhookId Parse(string s) => new(Guid.Parse(s));
}

/// <summary>
/// Unique identifier for an API key.
/// </summary>
public readonly record struct ApiKeyId(Guid Value)
{
    public static ApiKeyId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
    public static ApiKeyId Parse(string s) => new(Guid.Parse(s));
}

/// <summary>
/// Unique identifier for a sync job.
/// </summary>
public readonly record struct SyncJobId(Guid Value)
{
    public static SyncJobId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
    public static SyncJobId Parse(string s) => new(Guid.Parse(s));
}

// ============================================================================
// Enums
// ============================================================================

/// <summary>
/// Categories of integrations.
/// </summary>
public enum IntegrationCategory
{
    CloudProvider,      // AWS, Azure, GCP
    SourceControl,      // GitHub, GitLab, Bitbucket
    IssueTracking,      // Jira, Linear, Asana
    Monitoring,         // Datadog, New Relic, Prometheus
    Alerting,           // PagerDuty, OpsGenie, VictorOps
    Communication,      // Slack, Teams, Discord
    CI_CD,              // Jenkins, CircleCI, GitHub Actions
    Database,           // PostgreSQL, MySQL, MongoDB
    Storage,            // S3, Azure Blob, GCS
    Identity,           // Okta, Auth0, Azure AD
    Custom              // User-defined integrations
}

/// <summary>
/// Authentication methods supported by integrations.
/// </summary>
public enum AuthMethod
{
    None,
    ApiKey,
    OAuth2,
    OAuth2PKCE,
    BasicAuth,
    BearerToken,
    AWS_IAM,
    Azure_ServicePrincipal,
    GCP_ServiceAccount,
    Certificate,
    SAML,
    Custom
}

/// <summary>
/// Status of an integration instance.
/// </summary>
public enum IntegrationStatus
{
    Pending,
    Connecting,
    Connected,
    Disconnected,
    Error,
    Expired,
    Revoked,
    Suspended
}

/// <summary>
/// Health status of an integration.
/// </summary>
public enum IntegrationHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unhealthy,
    Unreachable
}

/// <summary>
/// Sync direction for data synchronization.
/// </summary>
public enum SyncDirection
{
    Inbound,    // From external system to Control Room
    Outbound,   // From Control Room to external system
    Bidirectional
}

/// <summary>
/// Status of a sync job.
/// </summary>
public enum SyncStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    PartialSuccess
}

/// <summary>
/// Webhook event types.
/// </summary>
public enum WebhookEventType
{
    // Generic
    Generic,

    // Source Control
    Push,
    PullRequest,
    PullRequestMerged,
    PullRequestClosed,
    BranchCreated,
    BranchDeleted,
    TagCreated,
    Release,

    // Issue Tracking
    IssueCreated,
    IssueUpdated,
    IssueClosed,
    IssueCommented,
    SprintStarted,
    SprintEnded,

    // CI/CD
    BuildStarted,
    BuildCompleted,
    BuildFailed,
    DeploymentStarted,
    DeploymentCompleted,
    DeploymentFailed,

    // Monitoring/Alerting
    AlertTriggered,
    AlertResolved,
    IncidentCreated,
    IncidentResolved,

    // Cloud
    ResourceCreated,
    ResourceUpdated,
    ResourceDeleted,
    CostAlert,
    SecurityAlert
}

/// <summary>
/// API key scope/permission.
/// </summary>
public enum ApiKeyScope
{
    Read,
    Write,
    Admin,
    Webhook,
    Integration,
    Full
}

// ============================================================================
// Core Records
// ============================================================================

/// <summary>
/// Definition of an integration (template/blueprint).
/// </summary>
public sealed record Integration(
    IntegrationId Id,
    string Name,
    string DisplayName,
    string Description,
    IntegrationCategory Category,
    AuthMethod AuthMethod,
    string IconUrl,
    string DocumentationUrl,
    bool IsBuiltIn,
    bool IsEnabled,
    IntegrationCapabilities Capabilities,
    IntegrationConfig Config,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt
);

/// <summary>
/// Capabilities that an integration supports.
/// </summary>
public sealed record IntegrationCapabilities(
    bool SupportsWebhooks,
    bool SupportsPush,
    bool SupportsPull,
    bool SupportsSync,
    bool SupportsEvents,
    bool SupportsActions,
    bool SupportsHealthCheck,
    IReadOnlyList<string> SupportedActions,
    IReadOnlyList<WebhookEventType> SupportedEvents
);

/// <summary>
/// Configuration schema for an integration.
/// </summary>
public sealed record IntegrationConfig(
    IReadOnlyList<ConfigField> Fields,
    IReadOnlyList<string> RequiredScopes,
    string? OAuthAuthorizationUrl,
    string? OAuthTokenUrl,
    string? OAuthRedirectUri,
    string? ApiBaseUrl,
    Dictionary<string, string> DefaultHeaders,
    int? RateLimitPerMinute,
    int? TimeoutMs
);

/// <summary>
/// A configuration field for an integration.
/// </summary>
public sealed record ConfigField(
    string Name,
    string DisplayName,
    string Description,
    ConfigFieldType Type,
    bool IsRequired,
    bool IsSecret,
    string? DefaultValue,
    string? ValidationPattern,
    IReadOnlyList<string>? AllowedValues
);

/// <summary>
/// Types of configuration fields.
/// </summary>
public enum ConfigFieldType
{
    String,
    Number,
    Boolean,
    Secret,
    Url,
    Email,
    Select,
    MultiSelect,
    Json,
    File
}

/// <summary>
/// A connected instance of an integration (actual connection).
/// </summary>
public sealed record IntegrationInstance(
    IntegrationInstanceId Id,
    IntegrationId IntegrationId,
    UserId OwnerId,
    TeamId? TeamId,
    string Name,
    string DisplayName,
    IntegrationStatus Status,
    IntegrationHealth Health,
    Dictionary<string, string> Configuration,
    CredentialInfo? Credentials,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastHealthCheckAt,
    string? LastError,
    Dictionary<string, object>? Metadata
);

/// <summary>
/// Secure credential storage info.
/// </summary>
public sealed record CredentialInfo(
    string EncryptedAccessToken,
    string? EncryptedRefreshToken,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes,
    string? TokenType,
    Dictionary<string, string>? AdditionalData
);

/// <summary>
/// Webhook endpoint configuration.
/// </summary>
public sealed record WebhookEndpoint(
    WebhookId Id,
    IntegrationInstanceId InstanceId,
    string Url,
    string Secret,
    IReadOnlyList<WebhookEventType> SubscribedEvents,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastReceivedAt,
    int TotalReceived,
    int TotalProcessed,
    int TotalFailed
);

/// <summary>
/// Incoming webhook event.
/// </summary>
public sealed record WebhookEvent(
    Guid Id,
    WebhookId WebhookId,
    IntegrationInstanceId InstanceId,
    WebhookEventType EventType,
    string RawPayload,
    Dictionary<string, string> Headers,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt,
    bool IsProcessed,
    string? Error
);

/// <summary>
/// API key for external access.
/// </summary>
public sealed record ApiKey(
    ApiKeyId Id,
    UserId OwnerId,
    TeamId? TeamId,
    string Name,
    string KeyPrefix,
    string HashedKey,
    IReadOnlyList<ApiKeyScope> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool IsActive,
    string? AllowedIps,
    int UsageCount
);

/// <summary>
/// Data synchronization job.
/// </summary>
public sealed record SyncJob(
    SyncJobId Id,
    IntegrationInstanceId InstanceId,
    SyncDirection Direction,
    string ResourceType,
    SyncStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int TotalRecords,
    int ProcessedRecords,
    int FailedRecords,
    string? Error,
    Dictionary<string, object>? Metadata
);

/// <summary>
/// Integration action that can be triggered.
/// </summary>
public sealed record IntegrationAction(
    string Name,
    string DisplayName,
    string Description,
    IReadOnlyList<ActionParameter> Parameters,
    string? ReturnType
);

/// <summary>
/// Parameter for an integration action.
/// </summary>
public sealed record ActionParameter(
    string Name,
    string DisplayName,
    string Description,
    ConfigFieldType Type,
    bool IsRequired,
    string? DefaultValue
);

/// <summary>
/// Result of executing an integration action.
/// </summary>
public sealed record ActionResult(
    bool Success,
    string? Message,
    object? Data,
    Dictionary<string, object>? Metadata,
    DateTimeOffset ExecutedAt,
    TimeSpan Duration
);

/// <summary>
/// Integration health check result.
/// </summary>
public sealed record IntegrationHealthCheck(
    IntegrationInstanceId InstanceId,
    IntegrationHealth Health,
    DateTimeOffset CheckedAt,
    TimeSpan ResponseTime,
    string? Message,
    Dictionary<string, object>? Details
);

// ============================================================================
// Event Records
// ============================================================================

/// <summary>
/// Integration event for audit/logging.
/// </summary>
public sealed record IntegrationEvent(
    Guid Id,
    IntegrationInstanceId InstanceId,
    IntegrationEventType EventType,
    string Description,
    UserId? TriggeredBy,
    DateTimeOffset OccurredAt,
    Dictionary<string, object>? Data
);

/// <summary>
/// Types of integration events.
/// </summary>
public enum IntegrationEventType
{
    Connected,
    Disconnected,
    Refreshed,
    Synced,
    ActionExecuted,
    WebhookReceived,
    HealthCheckPassed,
    HealthCheckFailed,
    Error,
    ConfigUpdated,
    CredentialsRotated
}

// ============================================================================
// Extension Methods
// ============================================================================

public static class IntegrationExtensions
{
    public static bool IsConnected(this IntegrationStatus status) =>
        status == IntegrationStatus.Connected;

    public static bool NeedsReconnection(this IntegrationStatus status) =>
        status is IntegrationStatus.Expired or IntegrationStatus.Revoked or IntegrationStatus.Error;

    public static bool IsHealthy(this IntegrationHealth health) =>
        health is IntegrationHealth.Healthy or IntegrationHealth.Unknown;

    public static bool RequiresOAuth(this AuthMethod method) =>
        method is AuthMethod.OAuth2 or AuthMethod.OAuth2PKCE;

    public static bool RequiresCredentials(this AuthMethod method) =>
        method is not AuthMethod.None;

    public static string GetCategoryIcon(this IntegrationCategory category) => category switch
    {
        IntegrationCategory.CloudProvider => "\u2601\uFE0F",
        IntegrationCategory.SourceControl => "\U0001F4BB",
        IntegrationCategory.IssueTracking => "\U0001F4CB",
        IntegrationCategory.Monitoring => "\U0001F4CA",
        IntegrationCategory.Alerting => "\U0001F6A8",
        IntegrationCategory.Communication => "\U0001F4AC",
        IntegrationCategory.CI_CD => "\u2699\uFE0F",
        IntegrationCategory.Database => "\U0001F5C4\uFE0F",
        IntegrationCategory.Storage => "\U0001F4E6",
        IntegrationCategory.Identity => "\U0001F511",
        IntegrationCategory.Custom => "\U0001F527",
        _ => "\U0001F517"
    };

    public static string GetStatusIcon(this IntegrationStatus status) => status switch
    {
        IntegrationStatus.Connected => "\u2705",
        IntegrationStatus.Connecting => "\U0001F504",
        IntegrationStatus.Pending => "\u23F3",
        IntegrationStatus.Disconnected => "\u26AA",
        IntegrationStatus.Error => "\u274C",
        IntegrationStatus.Expired => "\u23F0",
        IntegrationStatus.Revoked => "\U0001F6AB",
        IntegrationStatus.Suspended => "\u23F8\uFE0F",
        _ => "\u2753"
    };

    public static string GetHealthIcon(this IntegrationHealth health) => health switch
    {
        IntegrationHealth.Healthy => "\U0001F49A",
        IntegrationHealth.Degraded => "\U0001F7E1",
        IntegrationHealth.Unhealthy => "\U0001F534",
        IntegrationHealth.Unreachable => "\u26AB",
        IntegrationHealth.Unknown => "\u2754",
        _ => "\u2754"
    };

    public static string GetEventIcon(this WebhookEventType eventType) => eventType switch
    {
        WebhookEventType.Push => "\u2B06\uFE0F",
        WebhookEventType.PullRequest => "\U0001F500",
        WebhookEventType.PullRequestMerged => "\U0001F91D",
        WebhookEventType.Release => "\U0001F680",
        WebhookEventType.IssueCreated => "\U0001F4DD",
        WebhookEventType.IssueClosed => "\u2705",
        WebhookEventType.BuildStarted => "\U0001F3D7\uFE0F",
        WebhookEventType.BuildCompleted => "\U0001F3C1",
        WebhookEventType.BuildFailed => "\U0001F4A5",
        WebhookEventType.DeploymentStarted => "\U0001F6EB",
        WebhookEventType.DeploymentCompleted => "\U0001F6EC",
        WebhookEventType.DeploymentFailed => "\U0001F525",
        WebhookEventType.AlertTriggered => "\U0001F6A8",
        WebhookEventType.AlertResolved => "\U0001F49A",
        WebhookEventType.IncidentCreated => "\U0001F6A8",
        WebhookEventType.IncidentResolved => "\u2705",
        _ => "\U0001F4E9"
    };
}
