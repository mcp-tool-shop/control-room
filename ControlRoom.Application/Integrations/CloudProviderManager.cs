using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.Integrations;

/// <summary>
/// Manages cloud provider integrations and provides a unified interface.
/// </summary>
public sealed class CloudProviderManager
{
    private readonly Db _db;
    private readonly IntegrationQueries _queries;
    private readonly Dictionary<string, ICloudProvider> _providers = new();
    private readonly Dictionary<IntegrationInstanceId, ICloudProvider> _instanceProviders = new();

    // Events
    public event EventHandler<CloudResourceChangedEventArgs>? ResourceChanged;
    public event EventHandler<CloudAlertEventArgs>? AlertTriggered;

    public CloudProviderManager(Db db)
    {
        _db = db;
        _queries = new IntegrationQueries(db);

        // Register built-in providers
        RegisterProvider(new AwsProvider());
        RegisterProvider(new AzureProvider());
        RegisterProvider(new GcpProvider());
    }

    /// <summary>
    /// Register a cloud provider implementation.
    /// </summary>
    public void RegisterProvider(ICloudProvider provider)
    {
        _providers[provider.ProviderName] = provider;

        // Wire up events
        if (provider is AwsProvider awsProvider)
        {
            awsProvider.ResourceChanged += (s, e) => ResourceChanged?.Invoke(this, e);
            awsProvider.AlertTriggered += (s, e) => AlertTriggered?.Invoke(this, e);
        }
        else if (provider is AzureProvider azureProvider)
        {
            azureProvider.ResourceChanged += (s, e) => ResourceChanged?.Invoke(this, e);
            azureProvider.AlertTriggered += (s, e) => AlertTriggered?.Invoke(this, e);
        }
        else if (provider is GcpProvider gcpProvider)
        {
            gcpProvider.ResourceChanged += (s, e) => ResourceChanged?.Invoke(this, e);
            gcpProvider.AlertTriggered += (s, e) => AlertTriggered?.Invoke(this, e);
        }
    }

    /// <summary>
    /// Get a provider by name.
    /// </summary>
    public ICloudProvider? GetProvider(string providerName)
    {
        return _providers.GetValueOrDefault(providerName);
    }

    /// <summary>
    /// Get all registered providers.
    /// </summary>
    public IReadOnlyList<ICloudProvider> GetAllProviders()
    {
        return _providers.Values.ToList();
    }

    /// <summary>
    /// Initialize a provider for a specific integration instance.
    /// </summary>
    public async Task<ICloudProvider> InitializeProviderAsync(
        IntegrationInstanceId instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = _queries.GetInstance(instanceId)
            ?? throw new InvalidOperationException("Integration instance not found");

        var integration = _queries.GetIntegration(instance.IntegrationId)
            ?? throw new InvalidOperationException("Integration not found");

        var provider = GetProvider(integration.Name)
            ?? throw new InvalidOperationException($"No provider registered for {integration.Name}");

        // Configure the provider with instance credentials
        var configuration = new Dictionary<string, string>(instance.Configuration);

        // Add decrypted credentials if available
        if (instance.Credentials != null)
        {
            // In real implementation, would decrypt credentials
            configuration["access_token"] = instance.Credentials.EncryptedAccessToken;
            if (instance.Credentials.EncryptedRefreshToken != null)
            {
                configuration["refresh_token"] = instance.Credentials.EncryptedRefreshToken;
            }
        }

        // Validate credentials
        var validationResult = await provider.ValidateCredentialsAsync(configuration, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new CloudProviderException(provider.ProviderName, validationResult.ErrorMessage ?? "Invalid credentials");
        }

        _instanceProviders[instanceId] = provider;
        return provider;
    }

    /// <summary>
    /// Get an initialized provider for an instance.
    /// </summary>
    public ICloudProvider? GetProviderForInstance(IntegrationInstanceId instanceId)
    {
        return _instanceProviders.GetValueOrDefault(instanceId);
    }

    // ========================================================================
    // Unified Multi-Cloud Operations
    // ========================================================================

    /// <summary>
    /// List instances across all connected cloud providers.
    /// </summary>
    public async Task<MultiCloudResourceList<ComputeInstance>> ListAllInstancesAsync(
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        var allInstances = new List<ProviderResource<ComputeInstance>>();
        var errors = new Dictionary<string, string>();

        foreach (var (instanceId, provider) in _instanceProviders)
        {
            try
            {
                var result = await provider.ListInstancesAsync(region ?? "", cancellationToken);
                allInstances.AddRange(result.Items.Select(i => new ProviderResource<ComputeInstance>(
                    provider.ProviderName,
                    instanceId,
                    i)));
            }
            catch (Exception ex)
            {
                errors[provider.ProviderName] = ex.Message;
            }
        }

        return new MultiCloudResourceList<ComputeInstance>(
            allInstances,
            allInstances.Count,
            errors,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// List storage buckets across all connected cloud providers.
    /// </summary>
    public async Task<MultiCloudResourceList<StorageBucket>> ListAllStorageBucketsAsync(
        CancellationToken cancellationToken = default)
    {
        var allBuckets = new List<ProviderResource<StorageBucket>>();
        var errors = new Dictionary<string, string>();

        foreach (var (instanceId, provider) in _instanceProviders)
        {
            try
            {
                var result = await provider.ListStorageBucketsAsync(cancellationToken);
                allBuckets.AddRange(result.Items.Select(b => new ProviderResource<StorageBucket>(
                    provider.ProviderName,
                    instanceId,
                    b)));
            }
            catch (Exception ex)
            {
                errors[provider.ProviderName] = ex.Message;
            }
        }

        return new MultiCloudResourceList<StorageBucket>(
            allBuckets,
            allBuckets.Count,
            errors,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// List databases across all connected cloud providers.
    /// </summary>
    public async Task<MultiCloudResourceList<CloudDatabase>> ListAllDatabasesAsync(
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        var allDatabases = new List<ProviderResource<CloudDatabase>>();
        var errors = new Dictionary<string, string>();

        foreach (var (instanceId, provider) in _instanceProviders)
        {
            try
            {
                var result = await provider.ListDatabasesAsync(region ?? "", cancellationToken);
                allDatabases.AddRange(result.Items.Select(d => new ProviderResource<CloudDatabase>(
                    provider.ProviderName,
                    instanceId,
                    d)));
            }
            catch (Exception ex)
            {
                errors[provider.ProviderName] = ex.Message;
            }
        }

        return new MultiCloudResourceList<CloudDatabase>(
            allDatabases,
            allDatabases.Count,
            errors,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Get aggregated cost summary across all cloud providers.
    /// </summary>
    public async Task<MultiCloudCostSummary> GetAggregatedCostSummaryAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        var providerCosts = new List<ProviderCostSummary>();
        var errors = new Dictionary<string, string>();

        foreach (var (instanceId, provider) in _instanceProviders)
        {
            try
            {
                var cost = await provider.GetCostSummaryAsync(startDate, endDate, cancellationToken);
                providerCosts.Add(new ProviderCostSummary(
                    provider.ProviderName,
                    instanceId,
                    cost));
            }
            catch (Exception ex)
            {
                errors[provider.ProviderName] = ex.Message;
            }
        }

        var totalCost = providerCosts.Sum(c => c.Cost.TotalCost);

        return new MultiCloudCostSummary(
            totalCost,
            "USD", // Assuming normalized to USD
            startDate,
            endDate,
            providerCosts,
            errors,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Check health of all connected cloud integrations.
    /// </summary>
    public async Task<IReadOnlyList<ProviderHealthStatus>> CheckAllHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var healthStatuses = new List<ProviderHealthStatus>();

        foreach (var (instanceId, provider) in _instanceProviders)
        {
            var startTime = DateTimeOffset.UtcNow;

            try
            {
                // Validate credentials as health check
                var instance = _queries.GetInstance(instanceId);
                var config = instance?.Configuration ?? new();

                var validationResult = await provider.ValidateCredentialsAsync(config, cancellationToken);
                var responseTime = DateTimeOffset.UtcNow - startTime;

                healthStatuses.Add(new ProviderHealthStatus(
                    provider.ProviderName,
                    instanceId,
                    validationResult.IsValid ? IntegrationHealth.Healthy : IntegrationHealth.Unhealthy,
                    responseTime,
                    validationResult.ErrorMessage,
                    DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                var responseTime = DateTimeOffset.UtcNow - startTime;
                healthStatuses.Add(new ProviderHealthStatus(
                    provider.ProviderName,
                    instanceId,
                    IntegrationHealth.Unreachable,
                    responseTime,
                    ex.Message,
                    DateTimeOffset.UtcNow));
            }
        }

        return healthStatuses;
    }

    /// <summary>
    /// Execute an action on a specific instance.
    /// </summary>
    public async Task<CloudOperationResult> ExecuteInstanceActionAsync(
        IntegrationInstanceId integrationInstanceId,
        string instanceId,
        string region,
        CloudInstanceAction action,
        CancellationToken cancellationToken = default)
    {
        var provider = _instanceProviders.GetValueOrDefault(integrationInstanceId)
            ?? throw new InvalidOperationException("Provider not initialized for this instance");

        return action switch
        {
            CloudInstanceAction.Start => await provider.StartInstanceAsync(instanceId, region, cancellationToken),
            CloudInstanceAction.Stop => await provider.StopInstanceAsync(instanceId, region, cancellationToken),
            CloudInstanceAction.Restart => await provider.RestartInstanceAsync(instanceId, region, cancellationToken),
            _ => throw new ArgumentException($"Unknown action: {action}")
        };
    }

    /// <summary>
    /// Get metrics for a resource from any provider.
    /// </summary>
    public async Task<CloudMetrics> GetResourceMetricsAsync(
        IntegrationInstanceId integrationInstanceId,
        string resourceId,
        string metricName,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default)
    {
        var provider = _instanceProviders.GetValueOrDefault(integrationInstanceId)
            ?? throw new InvalidOperationException("Provider not initialized for this instance");

        return await provider.GetMetricsAsync(resourceId, metricName, startTime, endTime, cancellationToken);
    }

    /// <summary>
    /// Get all available regions across all providers.
    /// </summary>
    public async Task<MultiCloudRegionList> GetAllRegionsAsync(
        CancellationToken cancellationToken = default)
    {
        var allRegions = new List<ProviderResource<CloudRegion>>();

        foreach (var (name, provider) in _providers)
        {
            try
            {
                var regions = await provider.ListRegionsAsync(cancellationToken);
                allRegions.AddRange(regions.Select(r => new ProviderResource<CloudRegion>(
                    name,
                    default,
                    r)));
            }
            catch
            {
                // Skip providers that fail
            }
        }

        return new MultiCloudRegionList(allRegions, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Disconnect and remove a provider for an instance.
    /// </summary>
    public void DisconnectInstance(IntegrationInstanceId instanceId)
    {
        _instanceProviders.Remove(instanceId);
    }
}

// ========================================================================
// Multi-Cloud Types
// ========================================================================

/// <summary>
/// Resource with provider information.
/// </summary>
public sealed record ProviderResource<T>(
    string ProviderName,
    IntegrationInstanceId InstanceId,
    T Resource);

/// <summary>
/// Multi-cloud resource list.
/// </summary>
public sealed record MultiCloudResourceList<T>(
    IReadOnlyList<ProviderResource<T>> Items,
    int TotalCount,
    Dictionary<string, string> Errors,
    DateTimeOffset RetrievedAt);

/// <summary>
/// Provider cost summary.
/// </summary>
public sealed record ProviderCostSummary(
    string ProviderName,
    IntegrationInstanceId InstanceId,
    CloudCostSummary Cost);

/// <summary>
/// Aggregated multi-cloud cost summary.
/// </summary>
public sealed record MultiCloudCostSummary(
    decimal TotalCost,
    string Currency,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    IReadOnlyList<ProviderCostSummary> ProviderCosts,
    Dictionary<string, string> Errors,
    DateTimeOffset RetrievedAt);

/// <summary>
/// Provider health status.
/// </summary>
public sealed record ProviderHealthStatus(
    string ProviderName,
    IntegrationInstanceId InstanceId,
    IntegrationHealth Health,
    TimeSpan ResponseTime,
    string? Message,
    DateTimeOffset CheckedAt);

/// <summary>
/// Multi-cloud region list.
/// </summary>
public sealed record MultiCloudRegionList(
    IReadOnlyList<ProviderResource<CloudRegion>> Regions,
    DateTimeOffset RetrievedAt);

/// <summary>
/// Actions that can be performed on cloud instances.
/// </summary>
public enum CloudInstanceAction
{
    Start,
    Stop,
    Restart
}
