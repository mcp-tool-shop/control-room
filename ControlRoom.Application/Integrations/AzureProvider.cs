using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlRoom.Application.Integrations;

/// <summary>
/// Azure cloud provider integration.
/// Supports Virtual Machines, Storage, SQL Database, Monitor, and Cost Management.
/// </summary>
public sealed class AzureProvider : ICloudProvider
{
    private readonly HttpClient _httpClient;
    private string? _tenantId;
    private string? _clientId;
    private string? _clientSecret;
    private string? _subscriptionId;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry;

    public string ProviderName => "azure";

    private const string ManagementEndpoint = "https://management.azure.com";
    private const string AuthEndpoint = "https://login.microsoftonline.com";
    private const string ApiVersion = "2023-07-01";

    // Events
    public event EventHandler<CloudResourceChangedEventArgs>? ResourceChanged;
    public event EventHandler<CloudAlertEventArgs>? AlertTriggered;

    public AzureProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Configure the provider with service principal credentials.
    /// </summary>
    public void Configure(string tenantId, string clientId, string clientSecret, string subscriptionId)
    {
        _tenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _subscriptionId = subscriptionId;
        _accessToken = null; // Reset token
    }

    public async Task<CloudValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.TryGetValue("tenant_id", out var tenantId) ||
            !configuration.TryGetValue("client_id", out var clientId) ||
            !configuration.TryGetValue("client_secret", out var clientSecret) ||
            !configuration.TryGetValue("subscription_id", out var subscriptionId))
        {
            return new CloudValidationResult(
                false,
                "Missing required credentials: tenant_id, client_id, client_secret, subscription_id",
                null, null, []);
        }

        Configure(tenantId, clientId, clientSecret, subscriptionId);

        try
        {
            await EnsureAuthenticatedAsync(cancellationToken);

            // Validate by getting subscription info
            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"/subscriptions/{_subscriptionId}?api-version=2022-12-01",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new CloudValidationResult(
                    false,
                    $"Azure credential validation failed: {response.StatusCode}",
                    null, null, []);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var subscription = JsonSerializer.Deserialize<AzureSubscription>(content);

            return new CloudValidationResult(
                true,
                null,
                _subscriptionId,
                subscription?.DisplayName ?? $"Azure Subscription {_subscriptionId}",
                GetSupportedRegions(),
                new() { ["service"] = "Azure", ["state"] = subscription?.State ?? "Unknown" });
        }
        catch (Exception ex)
        {
            return new CloudValidationResult(
                false,
                $"Failed to validate Azure credentials: {ex.Message}",
                null, null, []);
        }
    }

    public async Task<CloudResourceList<ComputeInstance>> ListInstancesAsync(
        string region,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"/subscriptions/{_subscriptionId}/providers/Microsoft.Compute/virtualMachines?api-version={ApiVersion}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new CloudProviderException("Azure", $"Failed to list VMs: {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var vmList = JsonSerializer.Deserialize<AzureVmListResponse>(content);

            var instances = (vmList?.Value ?? [])
                .Where(vm => string.IsNullOrEmpty(region) || vm.Location == region)
                .Select(MapToComputeInstance)
                .ToList();

            return new CloudResourceList<ComputeInstance>(
                instances,
                instances.Count,
                vmList?.NextLink,
                DateTimeOffset.UtcNow);
        }
        catch (CloudProviderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("Azure", $"Error listing VMs: {ex.Message}", ex);
        }
    }

    public async Task<ComputeInstance?> GetInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            // instanceId should be the full resource ID for Azure
            var resourcePath = instanceId.StartsWith("/subscriptions/")
                ? instanceId
                : $"/subscriptions/{_subscriptionId}/resourceGroups/default/providers/Microsoft.Compute/virtualMachines/{instanceId}";

            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"{resourcePath}?api-version={ApiVersion}&$expand=instanceView",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var vm = JsonSerializer.Deserialize<AzureVm>(content);

            return vm != null ? MapToComputeInstance(vm) : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<CloudOperationResult> StartInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteVmAction(instanceId, "start", cancellationToken);
    }

    public async Task<CloudOperationResult> StopInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteVmAction(instanceId, "deallocate", cancellationToken);
    }

    public async Task<CloudOperationResult> RestartInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteVmAction(instanceId, "restart", cancellationToken);
    }

    public async Task<CloudMetrics> GetMetricsAsync(
        string resourceId,
        string metricName,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            var timespan = $"{startTime:yyyy-MM-ddTHH:mm:ssZ}/{endTime:yyyy-MM-ddTHH:mm:ssZ}";
            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"{resourceId}/providers/microsoft.insights/metrics?api-version=2023-10-01&metricnames={metricName}&timespan={timespan}&interval=PT5M&aggregation=Average,Minimum,Maximum",
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var metricsResponse = JsonSerializer.Deserialize<AzureMetricsResponse>(content);

            var dataPoints = new List<MetricDataPoint>();
            var metric = metricsResponse?.Value?.FirstOrDefault();

            if (metric?.Timeseries != null)
            {
                foreach (var ts in metric.Timeseries)
                {
                    foreach (var data in ts.Data ?? [])
                    {
                        dataPoints.Add(new MetricDataPoint(
                            data.TimeStamp,
                            data.Average,
                            data.Minimum,
                            data.Maximum,
                            null,
                            null));
                    }
                }
            }

            return new CloudMetrics(
                resourceId,
                metricName,
                metric?.Unit ?? "Count",
                dataPoints,
                startTime,
                endTime);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("Azure", $"Error getting metrics: {ex.Message}", ex);
        }
    }

    public Task<IReadOnlyList<CloudRegion>> ListRegionsAsync(
        CancellationToken cancellationToken = default)
    {
        var regions = GetSupportedRegions().Select(r => new CloudRegion(
            r,
            r,
            GetRegionDisplayName(r),
            GetRegionGeography(r),
            true,
            GetAvailabilityZones(r)
        )).ToList();

        return Task.FromResult<IReadOnlyList<CloudRegion>>(regions);
    }

    public async Task<CloudCostSummary> GetCostSummaryAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            var body = new
            {
                type = "ActualCost",
                timeframe = "Custom",
                timePeriod = new
                {
                    from = startDate.ToString("yyyy-MM-dd"),
                    to = endDate.ToString("yyyy-MM-dd")
                },
                dataset = new
                {
                    granularity = "Daily",
                    aggregation = new
                    {
                        totalCost = new { name = "Cost", function = "Sum" }
                    },
                    grouping = new[]
                    {
                        new { type = "Dimension", name = "ServiceName" }
                    }
                }
            };

            var response = await MakeRequestAsync(
                HttpMethod.Post,
                $"/subscriptions/{_subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-11-01",
                cancellationToken,
                JsonSerializer.Serialize(body));

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            // Parse cost data...

            return new CloudCostSummary(
                0m,
                "USD",
                startDate,
                endDate,
                [],
                [],
                null,
                new() { ["source"] = "Cost Management" });
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("Azure", $"Error getting cost summary: {ex.Message}", ex);
        }
    }

    public async Task<CloudResourceList<StorageBucket>> ListStorageBucketsAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"/subscriptions/{_subscriptionId}/providers/Microsoft.Storage/storageAccounts?api-version=2023-01-01",
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var storageList = JsonSerializer.Deserialize<AzureStorageAccountListResponse>(content);

            var buckets = (storageList?.Value ?? [])
                .Select(MapToStorageBucket)
                .ToList();

            return new CloudResourceList<StorageBucket>(
                buckets,
                buckets.Count,
                storageList?.NextLink,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("Azure", $"Error listing storage accounts: {ex.Message}", ex);
        }
    }

    public async Task<CloudResourceList<CloudDatabase>> ListDatabasesAsync(
        string region,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            // List SQL servers first, then databases
            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"/subscriptions/{_subscriptionId}/providers/Microsoft.Sql/servers?api-version=2023-05-01-preview",
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var serverList = JsonSerializer.Deserialize<AzureSqlServerListResponse>(content);

            var databases = new List<CloudDatabase>();

            foreach (var server in serverList?.Value ?? [])
            {
                if (!string.IsNullOrEmpty(region) && server.Location != region)
                    continue;

                var dbResponse = await MakeRequestAsync(
                    HttpMethod.Get,
                    $"{server.Id}/databases?api-version=2023-05-01-preview",
                    cancellationToken);

                var dbContent = await dbResponse.Content.ReadAsStringAsync(cancellationToken);
                var dbList = JsonSerializer.Deserialize<AzureSqlDatabaseListResponse>(dbContent);

                databases.AddRange((dbList?.Value ?? []).Select(db => MapToCloudDatabase(db, server)));
            }

            return new CloudResourceList<CloudDatabase>(
                databases,
                databases.Count,
                null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("Azure", $"Error listing databases: {ex.Message}", ex);
        }
    }

    // ========================================================================
    // Azure-Specific Operations
    // ========================================================================

    /// <summary>
    /// Lists resource groups.
    /// </summary>
    public async Task<IReadOnlyList<AzureResourceGroup>> ListResourceGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/subscriptions/{_subscriptionId}/resourcegroups?api-version=2022-09-01",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var rgList = JsonSerializer.Deserialize<AzureResourceGroupListResponse>(content);

        return rgList?.Value ?? [];
    }

    /// <summary>
    /// Lists network security groups.
    /// </summary>
    public async Task<IReadOnlyList<AzureNsg>> ListNetworkSecurityGroupsAsync(
        string? resourceGroup = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        var path = resourceGroup != null
            ? $"/subscriptions/{_subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Network/networkSecurityGroups?api-version=2023-09-01"
            : $"/subscriptions/{_subscriptionId}/providers/Microsoft.Network/networkSecurityGroups?api-version=2023-09-01";

        var response = await MakeRequestAsync(HttpMethod.Get, path, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var nsgList = JsonSerializer.Deserialize<AzureNsgListResponse>(content);

        return nsgList?.Value ?? [];
    }

    /// <summary>
    /// Creates an Azure Monitor alert rule.
    /// </summary>
    public async Task<CloudOperationResult> CreateAlertRuleAsync(
        string resourceGroup,
        AzureAlertRuleConfig config,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        var body = new
        {
            location = "global",
            properties = new
            {
                description = config.Description,
                severity = config.Severity,
                enabled = true,
                scopes = config.Scopes,
                evaluationFrequency = config.EvaluationFrequency,
                windowSize = config.WindowSize,
                criteria = new
                {
                    allOf = new[]
                    {
                        new
                        {
                            metricName = config.MetricName,
                            metricNamespace = config.MetricNamespace,
                            @operator = config.Operator,
                            threshold = config.Threshold,
                            timeAggregation = config.TimeAggregation
                        }
                    }
                }
            }
        };

        var response = await MakeRequestAsync(
            HttpMethod.Put,
            $"/subscriptions/{_subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Insights/metricAlerts/{config.Name}?api-version=2018-03-01",
            cancellationToken,
            JsonSerializer.Serialize(body));

        return new CloudOperationResult(
            response.IsSuccessStatusCode,
            null,
            response.IsSuccessStatusCode ? "Alert rule created" : "Failed to create alert rule",
            response.IsSuccessStatusCode ? CloudOperationStatus.Completed : CloudOperationStatus.Failed);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_tenantId) || string.IsNullOrEmpty(_clientId) ||
            string.IsNullOrEmpty(_clientSecret) || string.IsNullOrEmpty(_subscriptionId))
        {
            throw new InvalidOperationException("Azure credentials not configured. Call Configure() first.");
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken) && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return; // Token still valid
        }

        var tokenEndpoint = $"{AuthEndpoint}/{_tenantId}/oauth2/v2.0/token";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
            ["scope"] = "https://management.azure.com/.default"
        });

        var response = await _httpClient.PostAsync(tokenEndpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CloudProviderException("Azure", "Failed to acquire access token");
        }

        var tokenJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<AzureTokenResponse>(tokenJson);

        _accessToken = tokenResponse?.AccessToken;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(tokenResponse?.ExpiresIn ?? 3600);
    }

    private async Task<HttpResponseMessage> MakeRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken,
        string? body = null)
    {
        var uri = path.StartsWith("http") ? path : $"{ManagementEndpoint}{path}";
        var request = new HttpRequestMessage(method, uri);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<CloudOperationResult> ExecuteVmAction(
        string instanceId,
        string action,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            var resourcePath = instanceId.StartsWith("/subscriptions/")
                ? instanceId
                : $"/subscriptions/{_subscriptionId}/resourceGroups/default/providers/Microsoft.Compute/virtualMachines/{instanceId}";

            var response = await MakeRequestAsync(
                HttpMethod.Post,
                $"{resourcePath}/{action}?api-version={ApiVersion}",
                cancellationToken);

            // Get operation ID from async operation header
            var operationId = response.Headers.TryGetValues("Azure-AsyncOperation", out var asyncOps)
                ? asyncOps.FirstOrDefault()
                : null;

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                ResourceChanged?.Invoke(this, new CloudResourceChangedEventArgs
                {
                    ProviderId = "azure",
                    ResourceId = instanceId,
                    ResourceType = "VirtualMachine",
                    ChangeType = action,
                    OccurredAt = DateTimeOffset.UtcNow
                });
            }

            return new CloudOperationResult(
                response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted,
                operationId,
                response.IsSuccessStatusCode ? $"{action} initiated" : "Operation failed",
                response.IsSuccessStatusCode ? CloudOperationStatus.InProgress : CloudOperationStatus.Failed);
        }
        catch (Exception ex)
        {
            return new CloudOperationResult(
                false,
                null,
                ex.Message,
                CloudOperationStatus.Failed);
        }
    }

    private static ComputeInstance MapToComputeInstance(AzureVm vm)
    {
        var state = vm.Properties?.InstanceView?.Statuses?
            .FirstOrDefault(s => s.Code?.StartsWith("PowerState/") == true)?.Code switch
        {
            "PowerState/running" => ComputeInstanceState.Running,
            "PowerState/stopped" => ComputeInstanceState.Stopped,
            "PowerState/deallocated" => ComputeInstanceState.Stopped,
            "PowerState/starting" => ComputeInstanceState.Pending,
            "PowerState/stopping" => ComputeInstanceState.Stopping,
            _ => ComputeInstanceState.Unknown
        };

        var vmSize = vm.Properties?.HardwareProfile?.VmSize ?? "Unknown";

        return new ComputeInstance(
            vm.Id ?? "",
            vm.Name ?? "",
            vmSize,
            state,
            vm.Location ?? "",
            vm.Zones?.FirstOrDefault() ?? "",
            vm.Properties?.NetworkProfile?.NetworkInterfaces?.FirstOrDefault()?.Properties?.PrivateIpAddress,
            vm.Properties?.NetworkProfile?.NetworkInterfaces?.FirstOrDefault()?.Properties?.PublicIpAddress,
            null, // VPC equivalent
            null, // Subnet
            vm.Properties?.StorageProfile?.ImageReference?.Id,
            vm.Properties?.StorageProfile?.OsDisk?.OsType,
            GetVmCpuCores(vmSize),
            GetVmMemoryMb(vmSize),
            vm.Properties?.TimeCreated ?? DateTimeOffset.MinValue,
            vm.Tags ?? new(),
            new() { ["resourceGroup"] = ExtractResourceGroup(vm.Id) });
    }

    private static StorageBucket MapToStorageBucket(AzureStorageAccount account)
    {
        return new StorageBucket(
            account.Name ?? "",
            account.Location ?? "",
            account.Properties?.CreationTime ?? DateTimeOffset.MinValue,
            null, // Size not directly available
            null, // Object count not directly available
            account.Properties?.AccessTier?.ToLowerInvariant() switch
            {
                "hot" => StorageClass.HotTier,
                "cool" => StorageClass.CoolTier,
                "archive" => StorageClass.ArchiveTier,
                _ => StorageClass.Standard
            },
            account.Properties?.AllowBlobPublicAccess ?? false,
            false, // Versioning - would need separate call
            account.Tags ?? new());
    }

    private static CloudDatabase MapToCloudDatabase(AzureSqlDatabase db, AzureSqlServer server)
    {
        return new CloudDatabase(
            db.Id ?? "",
            db.Name ?? "",
            "SQL Server",
            server.Properties?.Version ?? "",
            db.Sku?.Name ?? "",
            db.Properties?.Status switch
            {
                "Online" => CloudDatabaseState.Available,
                "Creating" => CloudDatabaseState.Creating,
                "Deleting" => CloudDatabaseState.Deleting,
                "Paused" => CloudDatabaseState.Stopped,
                _ => CloudDatabaseState.Unknown
            },
            db.Location ?? "",
            null,
            (int)(db.Properties?.MaxSizeBytes / (1024 * 1024 * 1024) ?? 0),
            db.Properties?.ZoneRedundant ?? false,
            server.Properties?.FullyQualifiedDomainName,
            1433,
            db.Properties?.CreationDate ?? DateTimeOffset.MinValue,
            db.Tags ?? new());
    }

    private static string ExtractResourceGroup(string? resourceId)
    {
        if (string.IsNullOrEmpty(resourceId)) return "";
        var parts = resourceId.Split('/');
        var rgIndex = Array.IndexOf(parts, "resourceGroups");
        return rgIndex >= 0 && rgIndex + 1 < parts.Length ? parts[rgIndex + 1] : "";
    }

    private static int GetVmCpuCores(string vmSize)
    {
        // Simplified VM size parsing
        if (vmSize.Contains("Standard_B1")) return 1;
        if (vmSize.Contains("Standard_B2")) return 2;
        if (vmSize.Contains("Standard_D2")) return 2;
        if (vmSize.Contains("Standard_D4")) return 4;
        if (vmSize.Contains("Standard_D8")) return 8;
        return 2;
    }

    private static int GetVmMemoryMb(string vmSize)
    {
        if (vmSize.Contains("Standard_B1s")) return 1024;
        if (vmSize.Contains("Standard_B1ms")) return 2048;
        if (vmSize.Contains("Standard_B2s")) return 4096;
        if (vmSize.Contains("Standard_D2")) return 8192;
        if (vmSize.Contains("Standard_D4")) return 16384;
        return 4096;
    }

    private static IReadOnlyList<string> GetSupportedRegions() =>
    [
        "eastus", "eastus2", "westus", "westus2", "westus3",
        "centralus", "northcentralus", "southcentralus", "westcentralus",
        "canadacentral", "canadaeast",
        "northeurope", "westeurope", "uksouth", "ukwest",
        "francecentral", "germanywestcentral", "switzerlandnorth",
        "norwayeast", "swedencentral",
        "eastasia", "southeastasia",
        "japaneast", "japanwest",
        "australiaeast", "australiasoutheast",
        "koreacentral", "koreasouth",
        "centralindia", "southindia", "westindia",
        "brazilsouth"
    ];

    private static string GetRegionDisplayName(string region) => region switch
    {
        "eastus" => "East US",
        "eastus2" => "East US 2",
        "westus" => "West US",
        "westus2" => "West US 2",
        "westus3" => "West US 3",
        "centralus" => "Central US",
        "northeurope" => "North Europe",
        "westeurope" => "West Europe",
        "uksouth" => "UK South",
        "eastasia" => "East Asia",
        "southeastasia" => "Southeast Asia",
        "japaneast" => "Japan East",
        "australiaeast" => "Australia East",
        _ => region
    };

    private static string GetRegionGeography(string region)
    {
        if (region.Contains("us") || region.Contains("canada")) return "Americas";
        if (region.Contains("europe") || region.Contains("uk") || region.Contains("france") ||
            region.Contains("germany") || region.Contains("switzerland") || region.Contains("norway") ||
            region.Contains("sweden")) return "Europe";
        if (region.Contains("asia") || region.Contains("japan") || region.Contains("korea") ||
            region.Contains("india")) return "Asia Pacific";
        if (region.Contains("australia")) return "Australia";
        if (region.Contains("brazil")) return "South America";
        return "Unknown";
    }

    private static IReadOnlyList<string> GetAvailabilityZones(string region)
    {
        // Not all regions have AZs
        if (region is "eastus" or "eastus2" or "westus2" or "centralus" or
            "northeurope" or "westeurope" or "uksouth" or "francecentral" or
            "japaneast" or "australiaeast" or "southeastasia")
        {
            return ["1", "2", "3"];
        }
        return [];
    }
}

// ========================================================================
// Azure-Specific Types (DTOs for JSON deserialization)
// ========================================================================

internal sealed class AzureTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

internal sealed class AzureSubscription
{
    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }
}

internal sealed class AzureVmListResponse
{
    [JsonPropertyName("value")]
    public List<AzureVm>? Value { get; set; }

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

internal sealed class AzureVm
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("zones")]
    public List<string>? Zones { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }

    [JsonPropertyName("properties")]
    public AzureVmProperties? Properties { get; set; }
}

internal sealed class AzureVmProperties
{
    [JsonPropertyName("hardwareProfile")]
    public AzureHardwareProfile? HardwareProfile { get; set; }

    [JsonPropertyName("storageProfile")]
    public AzureStorageProfile? StorageProfile { get; set; }

    [JsonPropertyName("networkProfile")]
    public AzureNetworkProfile? NetworkProfile { get; set; }

    [JsonPropertyName("instanceView")]
    public AzureInstanceView? InstanceView { get; set; }

    [JsonPropertyName("timeCreated")]
    public DateTimeOffset? TimeCreated { get; set; }
}

internal sealed class AzureHardwareProfile
{
    [JsonPropertyName("vmSize")]
    public string? VmSize { get; set; }
}

internal sealed class AzureStorageProfile
{
    [JsonPropertyName("imageReference")]
    public AzureImageReference? ImageReference { get; set; }

    [JsonPropertyName("osDisk")]
    public AzureOsDisk? OsDisk { get; set; }
}

internal sealed class AzureImageReference
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class AzureOsDisk
{
    [JsonPropertyName("osType")]
    public string? OsType { get; set; }
}

internal sealed class AzureNetworkProfile
{
    [JsonPropertyName("networkInterfaces")]
    public List<AzureNetworkInterfaceRef>? NetworkInterfaces { get; set; }
}

internal sealed class AzureNetworkInterfaceRef
{
    [JsonPropertyName("properties")]
    public AzureNetworkInterfaceProperties? Properties { get; set; }
}

internal sealed class AzureNetworkInterfaceProperties
{
    [JsonPropertyName("privateIPAddress")]
    public string? PrivateIpAddress { get; set; }

    [JsonPropertyName("publicIPAddress")]
    public string? PublicIpAddress { get; set; }
}

internal sealed class AzureInstanceView
{
    [JsonPropertyName("statuses")]
    public List<AzureInstanceStatus>? Statuses { get; set; }
}

internal sealed class AzureInstanceStatus
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("displayStatus")]
    public string? DisplayStatus { get; set; }
}

internal sealed class AzureMetricsResponse
{
    [JsonPropertyName("value")]
    public List<AzureMetric>? Value { get; set; }
}

internal sealed class AzureMetric
{
    [JsonPropertyName("name")]
    public AzureMetricName? Name { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("timeseries")]
    public List<AzureTimeSeries>? Timeseries { get; set; }
}

internal sealed class AzureMetricName
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal sealed class AzureTimeSeries
{
    [JsonPropertyName("data")]
    public List<AzureMetricData>? Data { get; set; }
}

internal sealed class AzureMetricData
{
    [JsonPropertyName("timeStamp")]
    public DateTimeOffset TimeStamp { get; set; }

    [JsonPropertyName("average")]
    public double? Average { get; set; }

    [JsonPropertyName("minimum")]
    public double? Minimum { get; set; }

    [JsonPropertyName("maximum")]
    public double? Maximum { get; set; }
}

internal sealed class AzureStorageAccountListResponse
{
    [JsonPropertyName("value")]
    public List<AzureStorageAccount>? Value { get; set; }

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

internal sealed class AzureStorageAccount
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }

    [JsonPropertyName("properties")]
    public AzureStorageAccountProperties? Properties { get; set; }
}

internal sealed class AzureStorageAccountProperties
{
    [JsonPropertyName("creationTime")]
    public DateTimeOffset? CreationTime { get; set; }

    [JsonPropertyName("accessTier")]
    public string? AccessTier { get; set; }

    [JsonPropertyName("allowBlobPublicAccess")]
    public bool? AllowBlobPublicAccess { get; set; }
}

internal sealed class AzureSqlServerListResponse
{
    [JsonPropertyName("value")]
    public List<AzureSqlServer>? Value { get; set; }
}

internal sealed class AzureSqlServer
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("properties")]
    public AzureSqlServerProperties? Properties { get; set; }
}

internal sealed class AzureSqlServerProperties
{
    [JsonPropertyName("fullyQualifiedDomainName")]
    public string? FullyQualifiedDomainName { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

internal sealed class AzureSqlDatabaseListResponse
{
    [JsonPropertyName("value")]
    public List<AzureSqlDatabase>? Value { get; set; }
}

internal sealed class AzureSqlDatabase
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }

    [JsonPropertyName("sku")]
    public AzureSku? Sku { get; set; }

    [JsonPropertyName("properties")]
    public AzureSqlDatabaseProperties? Properties { get; set; }
}

internal sealed class AzureSku
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class AzureSqlDatabaseProperties
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("maxSizeBytes")]
    public long? MaxSizeBytes { get; set; }

    [JsonPropertyName("zoneRedundant")]
    public bool? ZoneRedundant { get; set; }

    [JsonPropertyName("creationDate")]
    public DateTimeOffset? CreationDate { get; set; }
}

internal sealed class AzureResourceGroupListResponse
{
    [JsonPropertyName("value")]
    public List<AzureResourceGroup>? Value { get; set; }
}

/// <summary>
/// Azure Resource Group.
/// </summary>
public sealed record AzureResourceGroup
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }

    [JsonPropertyName("properties")]
    public AzureResourceGroupProperties? Properties { get; init; }
}

/// <summary>
/// Azure Resource Group properties.
/// </summary>
public sealed record AzureResourceGroupProperties
{
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }
}

internal sealed class AzureNsgListResponse
{
    [JsonPropertyName("value")]
    public List<AzureNsg>? Value { get; set; }
}

/// <summary>
/// Azure Network Security Group.
/// </summary>
public sealed record AzureNsg
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// Configuration for Azure metric alert rule.
/// </summary>
public sealed record AzureAlertRuleConfig(
    string Name,
    string Description,
    int Severity,
    IReadOnlyList<string> Scopes,
    string MetricName,
    string MetricNamespace,
    string Operator,
    double Threshold,
    string TimeAggregation,
    string EvaluationFrequency = "PT5M",
    string WindowSize = "PT5M");
