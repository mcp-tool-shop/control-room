using ControlRoom.Domain.Model;

namespace ControlRoom.Application.Integrations;

// ============================================================================
// Cloud Provider Integration Interface
// ============================================================================

/// <summary>
/// Common interface for all cloud provider integrations.
/// </summary>
public interface ICloudProvider
{
    /// <summary>
    /// Gets the provider name (aws, azure, gcp).
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Validates the provided credentials.
    /// </summary>
    Task<CloudValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists compute instances.
    /// </summary>
    Task<CloudResourceList<ComputeInstance>> ListInstancesAsync(
        string region,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets details of a specific compute instance.
    /// </summary>
    Task<ComputeInstance?> GetInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a compute instance.
    /// </summary>
    Task<CloudOperationResult> StartInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a compute instance.
    /// </summary>
    Task<CloudOperationResult> StopInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts a compute instance.
    /// </summary>
    Task<CloudOperationResult> RestartInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current metrics for a resource.
    /// </summary>
    Task<CloudMetrics> GetMetricsAsync(
        string resourceId,
        string metricName,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists available regions.
    /// </summary>
    Task<IReadOnlyList<CloudRegion>> ListRegionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current cost information.
    /// </summary>
    Task<CloudCostSummary> GetCostSummaryAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists storage buckets/containers.
    /// </summary>
    Task<CloudResourceList<StorageBucket>> ListStorageBucketsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists databases.
    /// </summary>
    Task<CloudResourceList<CloudDatabase>> ListDatabasesAsync(
        string region,
        CancellationToken cancellationToken = default);
}

// ============================================================================
// Cloud Provider Data Types
// ============================================================================

/// <summary>
/// Result of credential validation.
/// </summary>
public sealed record CloudValidationResult(
    bool IsValid,
    string? ErrorMessage,
    string? AccountId,
    string? AccountName,
    IReadOnlyList<string> AvailableRegions,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Generic cloud resource list with pagination.
/// </summary>
public sealed record CloudResourceList<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    string? NextPageToken,
    DateTimeOffset RetrievedAt);

/// <summary>
/// Result of a cloud operation.
/// </summary>
public sealed record CloudOperationResult(
    bool Success,
    string? OperationId,
    string? Message,
    CloudOperationStatus Status,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Status of a cloud operation.
/// </summary>
public enum CloudOperationStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Compute instance details.
/// </summary>
public sealed record ComputeInstance(
    string Id,
    string Name,
    string InstanceType,
    ComputeInstanceState State,
    string Region,
    string AvailabilityZone,
    string? PrivateIp,
    string? PublicIp,
    string? VpcId,
    string? SubnetId,
    string? ImageId,
    string? Platform,
    int CpuCores,
    int MemoryMb,
    DateTimeOffset LaunchTime,
    Dictionary<string, string> Tags,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// State of a compute instance.
/// </summary>
public enum ComputeInstanceState
{
    Pending,
    Running,
    Stopping,
    Stopped,
    Terminated,
    ShuttingDown,
    Unknown
}

/// <summary>
/// Cloud region information.
/// </summary>
public sealed record CloudRegion(
    string Id,
    string Name,
    string DisplayName,
    string? GeographicLocation,
    bool IsEnabled,
    IReadOnlyList<string> AvailabilityZones);

/// <summary>
/// Cloud metrics data.
/// </summary>
public sealed record CloudMetrics(
    string ResourceId,
    string MetricName,
    string Unit,
    IReadOnlyList<MetricDataPoint> DataPoints,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Single metric data point.
/// </summary>
public sealed record MetricDataPoint(
    DateTimeOffset Timestamp,
    double? Average,
    double? Minimum,
    double? Maximum,
    double? Sum,
    int? SampleCount);

/// <summary>
/// Cloud cost summary.
/// </summary>
public sealed record CloudCostSummary(
    decimal TotalCost,
    string Currency,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    IReadOnlyList<CostByService> CostsByService,
    IReadOnlyList<CostByRegion> CostsByRegion,
    decimal? ForecastedCost,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Cost breakdown by service.
/// </summary>
public sealed record CostByService(
    string ServiceName,
    decimal Cost,
    decimal Percentage);

/// <summary>
/// Cost breakdown by region.
/// </summary>
public sealed record CostByRegion(
    string Region,
    decimal Cost,
    decimal Percentage);

/// <summary>
/// Storage bucket/container information.
/// </summary>
public sealed record StorageBucket(
    string Name,
    string Region,
    DateTimeOffset CreatedAt,
    long? SizeBytes,
    long? ObjectCount,
    StorageClass StorageClass,
    bool IsPublic,
    bool VersioningEnabled,
    Dictionary<string, string> Tags,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Storage class/tier.
/// </summary>
public enum StorageClass
{
    Standard,
    InfrequentAccess,
    Archive,
    Glacier,
    ColdLine,
    HotTier,
    CoolTier,
    ArchiveTier,
    Unknown
}

/// <summary>
/// Cloud database information.
/// </summary>
public sealed record CloudDatabase(
    string Id,
    string Name,
    string Engine,
    string EngineVersion,
    string InstanceClass,
    CloudDatabaseState State,
    string Region,
    string? AvailabilityZone,
    int StorageGb,
    bool MultiAz,
    string? Endpoint,
    int? Port,
    DateTimeOffset CreatedAt,
    Dictionary<string, string> Tags,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// State of a cloud database.
/// </summary>
public enum CloudDatabaseState
{
    Available,
    Creating,
    Deleting,
    Failed,
    Maintenance,
    Modifying,
    Stopped,
    Stopping,
    Starting,
    Unknown
}

// ============================================================================
// Cloud Provider Events
// ============================================================================

/// <summary>
/// Event raised when a cloud resource state changes.
/// </summary>
public sealed class CloudResourceChangedEventArgs : EventArgs
{
    public required string ProviderId { get; init; }
    public required string ResourceId { get; init; }
    public required string ResourceType { get; init; }
    public required string ChangeType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>
/// Event raised when a cloud alert is triggered.
/// </summary>
public sealed class CloudAlertEventArgs : EventArgs
{
    public required string ProviderId { get; init; }
    public required string AlertId { get; init; }
    public required string AlertName { get; init; }
    public required CloudAlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public required string ResourceId { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Severity of a cloud alert.
/// </summary>
public enum CloudAlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}
