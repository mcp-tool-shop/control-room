using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlRoom.Application.Integrations;

/// <summary>
/// Google Cloud Platform provider integration.
/// Supports Compute Engine, Cloud Storage, Cloud SQL, Cloud Monitoring, and Billing.
/// </summary>
public sealed class GcpProvider : ICloudProvider
{
    private readonly HttpClient _httpClient;
    private string? _projectId;
    private GcpServiceAccountKey? _serviceAccountKey;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry;

    public string ProviderName => "gcp";

    private const string ComputeEndpoint = "https://compute.googleapis.com/compute/v1";
    private const string StorageEndpoint = "https://storage.googleapis.com/storage/v1";
    private const string SqlEndpoint = "https://sqladmin.googleapis.com/v1";
    private const string MonitoringEndpoint = "https://monitoring.googleapis.com/v3";
    private const string BillingEndpoint = "https://cloudbilling.googleapis.com/v1";

    // Events
    public event EventHandler<CloudResourceChangedEventArgs>? ResourceChanged;
    public event EventHandler<CloudAlertEventArgs>? AlertTriggered;

    public GcpProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Configure the provider with service account credentials.
    /// </summary>
    public void Configure(string projectId, string serviceAccountKeyJson)
    {
        _projectId = projectId;
        _serviceAccountKey = JsonSerializer.Deserialize<GcpServiceAccountKey>(serviceAccountKeyJson);
        _accessToken = null; // Reset token
    }

    public async Task<CloudValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.TryGetValue("project_id", out var projectId) ||
            !configuration.TryGetValue("service_account_key", out var serviceAccountKey))
        {
            return new CloudValidationResult(
                false,
                "Missing required credentials: project_id and service_account_key",
                null, null, []);
        }

        try
        {
            Configure(projectId, serviceAccountKey);
            await EnsureAuthenticatedAsync(cancellationToken);

            // Validate by getting project info
            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"{ComputeEndpoint}/projects/{_projectId}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new CloudValidationResult(
                    false,
                    $"GCP credential validation failed: {response.StatusCode}",
                    null, null, []);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var project = JsonSerializer.Deserialize<GcpProject>(content);

            return new CloudValidationResult(
                true,
                null,
                _projectId,
                project?.Name ?? $"GCP Project {_projectId}",
                GetSupportedRegions(),
                new()
                {
                    ["service"] = "GCP",
                    ["serviceAccount"] = _serviceAccountKey?.ClientEmail ?? ""
                });
        }
        catch (Exception ex)
        {
            return new CloudValidationResult(
                false,
                $"Failed to validate GCP credentials: {ex.Message}",
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
            // GCP uses zones, not regions for instances. List instances across all zones in the region.
            var allInstances = new List<ComputeInstance>();

            // If region specified, get zones in that region; otherwise get all zones
            var zones = await GetZonesAsync(region, cancellationToken);

            foreach (var zone in zones)
            {
                var response = await MakeRequestAsync(
                    HttpMethod.Get,
                    $"{ComputeEndpoint}/projects/{_projectId}/zones/{zone}/instances",
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var instanceList = JsonSerializer.Deserialize<GcpInstanceListResponse>(content);

                    allInstances.AddRange((instanceList?.Items ?? []).Select(i => MapToComputeInstance(i, zone)));
                }
            }

            return new CloudResourceList<ComputeInstance>(
                allInstances,
                allInstances.Count,
                null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("GCP", $"Error listing instances: {ex.Message}", ex);
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
            // instanceId might be "zone/instance-name" format or just "instance-name"
            string zone, instanceName;
            if (instanceId.Contains('/'))
            {
                var parts = instanceId.Split('/');
                zone = parts[0];
                instanceName = parts[1];
            }
            else
            {
                // Need to search across zones
                zone = await FindInstanceZoneAsync(instanceId, region, cancellationToken) ?? "";
                instanceName = instanceId;
            }

            if (string.IsNullOrEmpty(zone)) return null;

            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"{ComputeEndpoint}/projects/{_projectId}/zones/{zone}/instances/{instanceName}",
                cancellationToken);

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var instance = JsonSerializer.Deserialize<GcpInstance>(content);

            return instance != null ? MapToComputeInstance(instance, zone) : null;
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
        return await ExecuteInstanceAction(instanceId, region, "start", cancellationToken);
    }

    public async Task<CloudOperationResult> StopInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteInstanceAction(instanceId, region, "stop", cancellationToken);
    }

    public async Task<CloudOperationResult> RestartInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteInstanceAction(instanceId, region, "reset", cancellationToken);
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
            var filter = $"metric.type=\"compute.googleapis.com/instance/{metricName}\"";
            var interval = $"startTime={startTime:yyyy-MM-ddTHH:mm:ssZ}&endTime={endTime:yyyy-MM-ddTHH:mm:ssZ}";

            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"{MonitoringEndpoint}/projects/{_projectId}/timeSeries?filter={Uri.EscapeDataString(filter)}&interval.{interval}",
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var metricsResponse = JsonSerializer.Deserialize<GcpTimeSeriesResponse>(content);

            var dataPoints = new List<MetricDataPoint>();
            foreach (var ts in metricsResponse?.TimeSeries ?? [])
            {
                foreach (var point in ts.Points ?? [])
                {
                    dataPoints.Add(new MetricDataPoint(
                        point.Interval?.EndTime ?? DateTimeOffset.MinValue,
                        point.Value?.DoubleValue ?? point.Value?.Int64Value,
                        null,
                        null,
                        null,
                        null));
                }
            }

            return new CloudMetrics(
                resourceId,
                metricName,
                metricsResponse?.TimeSeries?.FirstOrDefault()?.MetricKind ?? "GAUGE",
                dataPoints,
                startTime,
                endTime);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("GCP", $"Error getting metrics: {ex.Message}", ex);
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
            // GCP Billing API for cost analysis
            // Requires additional BigQuery export setup for detailed costs
            await Task.CompletedTask;

            return new CloudCostSummary(
                0m,
                "USD",
                startDate,
                endDate,
                [],
                [],
                null,
                new() { ["source"] = "Cloud Billing" });
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("GCP", $"Error getting cost summary: {ex.Message}", ex);
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
                $"{StorageEndpoint}/b?project={_projectId}",
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var bucketList = JsonSerializer.Deserialize<GcpBucketListResponse>(content);

            var buckets = (bucketList?.Items ?? [])
                .Select(MapToStorageBucket)
                .ToList();

            return new CloudResourceList<StorageBucket>(
                buckets,
                buckets.Count,
                bucketList?.NextPageToken,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("GCP", $"Error listing buckets: {ex.Message}", ex);
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
            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"{SqlEndpoint}/projects/{_projectId}/instances",
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var instanceList = JsonSerializer.Deserialize<GcpSqlInstanceListResponse>(content);

            var databases = (instanceList?.Items ?? [])
                .Where(db => string.IsNullOrEmpty(region) || db.Region == region)
                .Select(MapToCloudDatabase)
                .ToList();

            return new CloudResourceList<CloudDatabase>(
                databases,
                databases.Count,
                instanceList?.NextPageToken,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("GCP", $"Error listing databases: {ex.Message}", ex);
        }
    }

    // ========================================================================
    // GCP-Specific Operations
    // ========================================================================

    /// <summary>
    /// Lists GCP zones.
    /// </summary>
    public async Task<IReadOnlyList<GcpZone>> ListZonesAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"{ComputeEndpoint}/projects/{_projectId}/zones",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var zoneList = JsonSerializer.Deserialize<GcpZoneListResponse>(content);

        return zoneList?.Items ?? [];
    }

    /// <summary>
    /// Lists firewall rules.
    /// </summary>
    public async Task<IReadOnlyList<GcpFirewallRule>> ListFirewallRulesAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"{ComputeEndpoint}/projects/{_projectId}/global/firewalls",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var firewallList = JsonSerializer.Deserialize<GcpFirewallListResponse>(content);

        return firewallList?.Items ?? [];
    }

    /// <summary>
    /// Lists VPC networks.
    /// </summary>
    public async Task<IReadOnlyList<GcpNetwork>> ListNetworksAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"{ComputeEndpoint}/projects/{_projectId}/global/networks",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var networkList = JsonSerializer.Deserialize<GcpNetworkListResponse>(content);

        return networkList?.Items ?? [];
    }

    /// <summary>
    /// Creates a Cloud Monitoring alert policy.
    /// </summary>
    public async Task<CloudOperationResult> CreateAlertPolicyAsync(
        GcpAlertPolicyConfig config,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        var body = new
        {
            displayName = config.DisplayName,
            documentation = new { content = config.Documentation },
            conditions = new[]
            {
                new
                {
                    displayName = config.ConditionName,
                    conditionThreshold = new
                    {
                        filter = config.Filter,
                        comparison = config.Comparison,
                        thresholdValue = config.Threshold,
                        duration = config.Duration,
                        aggregations = new[]
                        {
                            new
                            {
                                alignmentPeriod = config.AlignmentPeriod,
                                perSeriesAligner = config.Aligner
                            }
                        }
                    }
                }
            },
            combiner = "OR",
            enabled = true
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"{MonitoringEndpoint}/projects/{_projectId}/alertPolicies",
            cancellationToken,
            JsonSerializer.Serialize(body));

        return new CloudOperationResult(
            response.IsSuccessStatusCode,
            null,
            response.IsSuccessStatusCode ? "Alert policy created" : "Failed to create alert policy",
            response.IsSuccessStatusCode ? CloudOperationStatus.Completed : CloudOperationStatus.Failed);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_projectId) || _serviceAccountKey == null)
        {
            throw new InvalidOperationException("GCP credentials not configured. Call Configure() first.");
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken) && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return; // Token still valid
        }

        // Create JWT for service account authentication
        var jwt = CreateServiceAccountJwt();

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = jwt
        });

        var response = await _httpClient.PostAsync(
            "https://oauth2.googleapis.com/token",
            content,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CloudProviderException("GCP", "Failed to acquire access token");
        }

        var tokenJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<GcpTokenResponse>(tokenJson);

        _accessToken = tokenResponse?.AccessToken;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(tokenResponse?.ExpiresIn ?? 3600);
    }

    private string CreateServiceAccountJwt()
    {
        var now = DateTimeOffset.UtcNow;
        var header = new { alg = "RS256", typ = "JWT" };
        var payload = new
        {
            iss = _serviceAccountKey!.ClientEmail,
            scope = "https://www.googleapis.com/auth/cloud-platform",
            aud = "https://oauth2.googleapis.com/token",
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddHours(1).ToUnixTimeSeconds()
        };

        var headerBase64 = Base64UrlEncode(JsonSerializer.Serialize(header));
        var payloadBase64 = Base64UrlEncode(JsonSerializer.Serialize(payload));
        var signatureInput = $"{headerBase64}.{payloadBase64}";

        // Sign with RSA private key
        var signature = SignWithPrivateKey(signatureInput, _serviceAccountKey.PrivateKey ?? "");
        var signatureBase64 = Base64UrlEncode(signature);

        return $"{headerBase64}.{payloadBase64}.{signatureBase64}";
    }

    private static string Base64UrlEncode(string input) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(input));

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static byte[] SignWithPrivateKey(string data, string privateKeyPem)
    {
        // Remove PEM headers/footers and decode
        var privateKeyBase64 = privateKeyPem
            .Replace("-----BEGIN PRIVATE KEY-----", "")
            .Replace("-----END PRIVATE KEY-----", "")
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "")
            .Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("\n", "")
            .Replace("\r", "")
            .Trim();

        try
        {
            var privateKeyBytes = Convert.FromBase64String(privateKeyBase64);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKeyBytes, out _);
            return rsa.SignData(Encoding.UTF8.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            // If key parsing fails, return empty signature (will fail auth)
            return [];
        }
    }

    private async Task<HttpResponseMessage> MakeRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken,
        string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetZonesAsync(string? region, CancellationToken cancellationToken)
    {
        var zones = await ListZonesAsync(cancellationToken);
        var zoneNames = zones
            .Where(z => z.Status == "UP")
            .Select(z => z.Name ?? "")
            .Where(n => !string.IsNullOrEmpty(n));

        if (!string.IsNullOrEmpty(region))
        {
            zoneNames = zoneNames.Where(z => z.StartsWith(region));
        }

        return zoneNames.ToList();
    }

    private async Task<string?> FindInstanceZoneAsync(string instanceName, string region, CancellationToken cancellationToken)
    {
        var zones = await GetZonesAsync(region, cancellationToken);
        foreach (var zone in zones)
        {
            var response = await MakeRequestAsync(
                HttpMethod.Get,
                $"{ComputeEndpoint}/projects/{_projectId}/zones/{zone}/instances/{instanceName}",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return zone;
            }
        }
        return null;
    }

    private async Task<CloudOperationResult> ExecuteInstanceAction(
        string instanceId,
        string region,
        string action,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            string zone, instanceName;
            if (instanceId.Contains('/'))
            {
                var parts = instanceId.Split('/');
                zone = parts[0];
                instanceName = parts[1];
            }
            else
            {
                zone = await FindInstanceZoneAsync(instanceId, region, cancellationToken) ?? "";
                instanceName = instanceId;
            }

            if (string.IsNullOrEmpty(zone))
            {
                return new CloudOperationResult(false, null, "Instance not found", CloudOperationStatus.Failed);
            }

            var response = await MakeRequestAsync(
                HttpMethod.Post,
                $"{ComputeEndpoint}/projects/{_projectId}/zones/{zone}/instances/{instanceName}/{action}",
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var operation = JsonSerializer.Deserialize<GcpOperation>(content);

            if (response.IsSuccessStatusCode)
            {
                ResourceChanged?.Invoke(this, new CloudResourceChangedEventArgs
                {
                    ProviderId = "gcp",
                    ResourceId = $"{zone}/{instanceName}",
                    ResourceType = "Instance",
                    ChangeType = action,
                    OccurredAt = DateTimeOffset.UtcNow
                });
            }

            return new CloudOperationResult(
                response.IsSuccessStatusCode,
                operation?.Name,
                response.IsSuccessStatusCode ? $"{action} initiated" : "Operation failed",
                response.IsSuccessStatusCode ? CloudOperationStatus.InProgress : CloudOperationStatus.Failed);
        }
        catch (Exception ex)
        {
            return new CloudOperationResult(false, null, ex.Message, CloudOperationStatus.Failed);
        }
    }

    private static ComputeInstance MapToComputeInstance(GcpInstance instance, string zone)
    {
        var state = instance.Status switch
        {
            "RUNNING" => ComputeInstanceState.Running,
            "STOPPED" => ComputeInstanceState.Stopped,
            "TERMINATED" => ComputeInstanceState.Terminated,
            "STAGING" or "PROVISIONING" => ComputeInstanceState.Pending,
            "STOPPING" or "SUSPENDING" => ComputeInstanceState.Stopping,
            _ => ComputeInstanceState.Unknown
        };

        var machineType = instance.MachineType?.Split('/').LastOrDefault() ?? "unknown";
        var region = zone.Length > 2 ? zone[..^2] : zone;

        // Parse machine type for CPU/memory
        var (cpuCores, memoryMb) = ParseMachineType(machineType);

        var networkInterface = instance.NetworkInterfaces?.FirstOrDefault();

        return new ComputeInstance(
            $"{zone}/{instance.Name}",
            instance.Name ?? "",
            machineType,
            state,
            region,
            zone,
            networkInterface?.NetworkIP,
            networkInterface?.AccessConfigs?.FirstOrDefault()?.NatIP,
            networkInterface?.Network?.Split('/').LastOrDefault(),
            networkInterface?.Subnetwork?.Split('/').LastOrDefault(),
            instance.Disks?.FirstOrDefault()?.Source,
            null, // GCP doesn't have explicit platform field
            cpuCores,
            memoryMb,
            instance.CreationTimestamp ?? DateTimeOffset.MinValue,
            instance.Labels ?? new(),
            new() { ["selfLink"] = instance.SelfLink ?? "" });
    }

    private static (int cpuCores, int memoryMb) ParseMachineType(string machineType)
    {
        // Parse standard GCP machine types (e.g., n1-standard-4, e2-medium)
        if (machineType.Contains("micro")) return (1, 614);
        if (machineType.Contains("small")) return (1, 1740);
        if (machineType.Contains("medium")) return (1, 3840);

        var parts = machineType.Split('-');
        if (parts.Length >= 3 && int.TryParse(parts[2], out var cores))
        {
            return parts[1] switch
            {
                "standard" => (cores, cores * 3840),
                "highmem" => (cores, cores * 6656),
                "highcpu" => (cores, cores * 896),
                _ => (cores, cores * 3840)
            };
        }

        return (2, 7680); // Default
    }

    private static StorageBucket MapToStorageBucket(GcpBucket bucket)
    {
        return new StorageBucket(
            bucket.Name ?? "",
            bucket.Location ?? "",
            bucket.TimeCreated ?? DateTimeOffset.MinValue,
            null, // Size requires separate API call
            null, // Object count requires separate API call
            bucket.StorageClass?.ToUpperInvariant() switch
            {
                "STANDARD" => StorageClass.Standard,
                "NEARLINE" => StorageClass.InfrequentAccess,
                "COLDLINE" => StorageClass.ColdLine,
                "ARCHIVE" => StorageClass.Archive,
                _ => StorageClass.Standard
            },
            bucket.IamConfiguration?.PublicAccessPrevention != "enforced",
            bucket.Versioning?.Enabled ?? false,
            bucket.Labels ?? new());
    }

    private static CloudDatabase MapToCloudDatabase(GcpSqlInstance instance)
    {
        return new CloudDatabase(
            instance.Name ?? "",
            instance.Name ?? "",
            instance.DatabaseVersion ?? "",
            instance.DatabaseVersion ?? "",
            instance.Settings?.Tier ?? "",
            instance.State switch
            {
                "RUNNABLE" => CloudDatabaseState.Available,
                "PENDING_CREATE" => CloudDatabaseState.Creating,
                "PENDING_DELETE" => CloudDatabaseState.Deleting,
                "MAINTENANCE" => CloudDatabaseState.Maintenance,
                "STOPPED" => CloudDatabaseState.Stopped,
                "FAILED" => CloudDatabaseState.Failed,
                _ => CloudDatabaseState.Unknown
            },
            instance.Region ?? "",
            instance.GceZone,
            (int)(instance.Settings?.DataDiskSizeGb ?? 0),
            instance.Settings?.AvailabilityType == "REGIONAL",
            instance.IpAddresses?.FirstOrDefault()?.IpAddress,
            3306, // MySQL default, would vary by database type
            instance.CreateTime ?? DateTimeOffset.MinValue,
            instance.Settings?.UserLabels ?? new());
    }

    private static IReadOnlyList<string> GetSupportedRegions() =>
    [
        "us-central1", "us-east1", "us-east4", "us-west1", "us-west2", "us-west3", "us-west4",
        "northamerica-northeast1", "northamerica-northeast2",
        "southamerica-east1", "southamerica-west1",
        "europe-west1", "europe-west2", "europe-west3", "europe-west4", "europe-west6",
        "europe-north1", "europe-central2",
        "asia-east1", "asia-east2", "asia-northeast1", "asia-northeast2", "asia-northeast3",
        "asia-south1", "asia-south2", "asia-southeast1", "asia-southeast2",
        "australia-southeast1", "australia-southeast2"
    ];

    private static string GetRegionDisplayName(string region) => region switch
    {
        "us-central1" => "Iowa",
        "us-east1" => "South Carolina",
        "us-east4" => "Northern Virginia",
        "us-west1" => "Oregon",
        "us-west2" => "Los Angeles",
        "europe-west1" => "Belgium",
        "europe-west2" => "London",
        "europe-west3" => "Frankfurt",
        "europe-west4" => "Netherlands",
        "asia-east1" => "Taiwan",
        "asia-northeast1" => "Tokyo",
        "asia-southeast1" => "Singapore",
        "australia-southeast1" => "Sydney",
        _ => region
    };

    private static string GetRegionGeography(string region)
    {
        if (region.StartsWith("us-") || region.StartsWith("northamerica-")) return "North America";
        if (region.StartsWith("southamerica-")) return "South America";
        if (region.StartsWith("europe-")) return "Europe";
        if (region.StartsWith("asia-")) return "Asia Pacific";
        if (region.StartsWith("australia-")) return "Australia";
        return "Unknown";
    }

    private static IReadOnlyList<string> GetAvailabilityZones(string region) =>
        new[] { $"{region}-a", $"{region}-b", $"{region}-c" };
}

// ========================================================================
// GCP-Specific Types (DTOs)
// ========================================================================

internal sealed class GcpServiceAccountKey
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("private_key_id")]
    public string? PrivateKeyId { get; set; }

    [JsonPropertyName("private_key")]
    public string? PrivateKey { get; set; }

    [JsonPropertyName("client_email")]
    public string? ClientEmail { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }
}

internal sealed class GcpTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}

internal sealed class GcpProject
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("selfLink")]
    public string? SelfLink { get; set; }
}

internal sealed class GcpInstanceListResponse
{
    [JsonPropertyName("items")]
    public List<GcpInstance>? Items { get; set; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

internal sealed class GcpInstance
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("machineType")]
    public string? MachineType { get; set; }

    [JsonPropertyName("zone")]
    public string? Zone { get; set; }

    [JsonPropertyName("selfLink")]
    public string? SelfLink { get; set; }

    [JsonPropertyName("creationTimestamp")]
    public DateTimeOffset? CreationTimestamp { get; set; }

    [JsonPropertyName("networkInterfaces")]
    public List<GcpNetworkInterface>? NetworkInterfaces { get; set; }

    [JsonPropertyName("disks")]
    public List<GcpDisk>? Disks { get; set; }

    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class GcpNetworkInterface
{
    [JsonPropertyName("networkIP")]
    public string? NetworkIP { get; set; }

    [JsonPropertyName("network")]
    public string? Network { get; set; }

    [JsonPropertyName("subnetwork")]
    public string? Subnetwork { get; set; }

    [JsonPropertyName("accessConfigs")]
    public List<GcpAccessConfig>? AccessConfigs { get; set; }
}

internal sealed class GcpAccessConfig
{
    [JsonPropertyName("natIP")]
    public string? NatIP { get; set; }
}

internal sealed class GcpDisk
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

internal sealed class GcpOperation
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

internal sealed class GcpTimeSeriesResponse
{
    [JsonPropertyName("timeSeries")]
    public List<GcpTimeSeries>? TimeSeries { get; set; }
}

internal sealed class GcpTimeSeries
{
    [JsonPropertyName("metricKind")]
    public string? MetricKind { get; set; }

    [JsonPropertyName("points")]
    public List<GcpPoint>? Points { get; set; }
}

internal sealed class GcpPoint
{
    [JsonPropertyName("interval")]
    public GcpInterval? Interval { get; set; }

    [JsonPropertyName("value")]
    public GcpTypedValue? Value { get; set; }
}

internal sealed class GcpInterval
{
    [JsonPropertyName("endTime")]
    public DateTimeOffset? EndTime { get; set; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; set; }
}

internal sealed class GcpTypedValue
{
    [JsonPropertyName("doubleValue")]
    public double? DoubleValue { get; set; }

    [JsonPropertyName("int64Value")]
    public long? Int64Value { get; set; }
}

internal sealed class GcpBucketListResponse
{
    [JsonPropertyName("items")]
    public List<GcpBucket>? Items { get; set; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

internal sealed class GcpBucket
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("storageClass")]
    public string? StorageClass { get; set; }

    [JsonPropertyName("timeCreated")]
    public DateTimeOffset? TimeCreated { get; set; }

    [JsonPropertyName("versioning")]
    public GcpVersioning? Versioning { get; set; }

    [JsonPropertyName("iamConfiguration")]
    public GcpIamConfiguration? IamConfiguration { get; set; }

    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class GcpVersioning
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}

internal sealed class GcpIamConfiguration
{
    [JsonPropertyName("publicAccessPrevention")]
    public string? PublicAccessPrevention { get; set; }
}

internal sealed class GcpSqlInstanceListResponse
{
    [JsonPropertyName("items")]
    public List<GcpSqlInstance>? Items { get; set; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

internal sealed class GcpSqlInstance
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("gceZone")]
    public string? GceZone { get; set; }

    [JsonPropertyName("databaseVersion")]
    public string? DatabaseVersion { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("createTime")]
    public DateTimeOffset? CreateTime { get; set; }

    [JsonPropertyName("settings")]
    public GcpSqlSettings? Settings { get; set; }

    [JsonPropertyName("ipAddresses")]
    public List<GcpSqlIpAddress>? IpAddresses { get; set; }
}

internal sealed class GcpSqlSettings
{
    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    [JsonPropertyName("dataDiskSizeGb")]
    public long? DataDiskSizeGb { get; set; }

    [JsonPropertyName("availabilityType")]
    public string? AvailabilityType { get; set; }

    [JsonPropertyName("userLabels")]
    public Dictionary<string, string>? UserLabels { get; set; }
}

internal sealed class GcpSqlIpAddress
{
    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

internal sealed class GcpZoneListResponse
{
    [JsonPropertyName("items")]
    public List<GcpZone>? Items { get; set; }
}

/// <summary>
/// GCP Zone.
/// </summary>
public sealed record GcpZone
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("region")]
    public string? Region { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

internal sealed class GcpFirewallListResponse
{
    [JsonPropertyName("items")]
    public List<GcpFirewallRule>? Items { get; set; }
}

/// <summary>
/// GCP Firewall Rule.
/// </summary>
public sealed record GcpFirewallRule
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("network")]
    public string? Network { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("priority")]
    public int? Priority { get; init; }

    [JsonPropertyName("sourceRanges")]
    public List<string>? SourceRanges { get; init; }

    [JsonPropertyName("allowed")]
    public List<GcpFirewallAllowed>? Allowed { get; init; }

    [JsonPropertyName("denied")]
    public List<GcpFirewallDenied>? Denied { get; init; }
}

/// <summary>
/// GCP Firewall allowed rule.
/// </summary>
public sealed record GcpFirewallAllowed
{
    [JsonPropertyName("IPProtocol")]
    public string? IpProtocol { get; init; }

    [JsonPropertyName("ports")]
    public List<string>? Ports { get; init; }
}

/// <summary>
/// GCP Firewall denied rule.
/// </summary>
public sealed record GcpFirewallDenied
{
    [JsonPropertyName("IPProtocol")]
    public string? IpProtocol { get; init; }

    [JsonPropertyName("ports")]
    public List<string>? Ports { get; init; }
}

internal sealed class GcpNetworkListResponse
{
    [JsonPropertyName("items")]
    public List<GcpNetwork>? Items { get; set; }
}

/// <summary>
/// GCP VPC Network.
/// </summary>
public sealed record GcpNetwork
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("selfLink")]
    public string? SelfLink { get; init; }

    [JsonPropertyName("autoCreateSubnetworks")]
    public bool? AutoCreateSubnetworks { get; init; }

    [JsonPropertyName("subnetworks")]
    public List<string>? Subnetworks { get; init; }
}

/// <summary>
/// Configuration for GCP alert policy.
/// </summary>
public sealed record GcpAlertPolicyConfig(
    string DisplayName,
    string ConditionName,
    string Filter,
    string Comparison,
    double Threshold,
    string Duration = "60s",
    string AlignmentPeriod = "60s",
    string Aligner = "ALIGN_MEAN",
    string? Documentation = null);
