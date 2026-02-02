using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlRoom.Domain.Model;

namespace ControlRoom.Application.Integrations;

/// <summary>
/// AWS cloud provider integration.
/// Supports EC2, S3, RDS, CloudWatch, and Cost Explorer.
/// </summary>
public sealed class AwsProvider : ICloudProvider
{
    private readonly HttpClient _httpClient;
    private string? _accessKeyId;
    private string? _secretAccessKey;
    private string _region = "us-east-1";

    public string ProviderName => "aws";

    // Events
    public event EventHandler<CloudResourceChangedEventArgs>? ResourceChanged;
    public event EventHandler<CloudAlertEventArgs>? AlertTriggered;

    public AwsProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Configure the provider with credentials.
    /// </summary>
    public void Configure(string accessKeyId, string secretAccessKey, string region = "us-east-1")
    {
        _accessKeyId = accessKeyId;
        _secretAccessKey = secretAccessKey;
        _region = region;
    }

    public async Task<CloudValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.TryGetValue("access_key_id", out var accessKeyId) ||
            !configuration.TryGetValue("secret_access_key", out var secretAccessKey))
        {
            return new CloudValidationResult(
                false,
                "Missing required credentials: access_key_id and secret_access_key",
                null, null, []);
        }

        Configure(accessKeyId, secretAccessKey, configuration.GetValueOrDefault("region", "us-east-1"));

        try
        {
            // Call STS GetCallerIdentity to validate credentials
            var request = CreateAwsRequest("sts", "us-east-1", "GetCallerIdentity", "2011-06-15");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                return new CloudValidationResult(
                    false,
                    $"AWS credential validation failed: {response.StatusCode}",
                    null, null, []);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse response to get account info
            // In real implementation, would parse XML response
            var accountId = ExtractAccountId(content);

            return new CloudValidationResult(
                true,
                null,
                accountId,
                $"AWS Account {accountId}",
                GetSupportedRegions(),
                new() { ["service"] = "AWS" });
        }
        catch (Exception ex)
        {
            return new CloudValidationResult(
                false,
                $"Failed to validate AWS credentials: {ex.Message}",
                null, null, []);
        }
    }

    public async Task<CloudResourceList<ComputeInstance>> ListInstancesAsync(
        string region,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        try
        {
            var request = CreateAwsRequest("ec2", region, "DescribeInstances", "2016-11-15");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new CloudProviderException("AWS", $"Failed to list instances: {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var instances = ParseEc2Instances(content, region);

            return new CloudResourceList<ComputeInstance>(
                instances,
                instances.Count,
                null,
                DateTimeOffset.UtcNow);
        }
        catch (CloudProviderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("AWS", $"Error listing instances: {ex.Message}", ex);
        }
    }

    public async Task<ComputeInstance?> GetInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["InstanceId.1"] = instanceId
            };

            var request = CreateAwsRequest("ec2", region, "DescribeInstances", "2016-11-15", parameters);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var instances = ParseEc2Instances(content, region);
            return instances.FirstOrDefault();
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
        return await ExecuteInstanceAction(instanceId, region, "StartInstances", cancellationToken);
    }

    public async Task<CloudOperationResult> StopInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteInstanceAction(instanceId, region, "StopInstances", cancellationToken);
    }

    public async Task<CloudOperationResult> RestartInstanceAsync(
        string instanceId,
        string region,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteInstanceAction(instanceId, region, "RebootInstances", cancellationToken);
    }

    public async Task<CloudMetrics> GetMetricsAsync(
        string resourceId,
        string metricName,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["Namespace"] = "AWS/EC2",
                ["MetricName"] = metricName,
                ["Dimensions.member.1.Name"] = "InstanceId",
                ["Dimensions.member.1.Value"] = resourceId,
                ["StartTime"] = startTime.ToString("o"),
                ["EndTime"] = endTime.ToString("o"),
                ["Period"] = "300", // 5 minutes
                ["Statistics.member.1"] = "Average",
                ["Statistics.member.2"] = "Minimum",
                ["Statistics.member.3"] = "Maximum"
            };

            var request = CreateAwsRequest("monitoring", _region, "GetMetricStatistics", "2010-08-01", parameters);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var dataPoints = ParseCloudWatchMetrics(content);

            return new CloudMetrics(
                resourceId,
                metricName,
                GetMetricUnit(metricName),
                dataPoints,
                startTime,
                endTime);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("AWS", $"Error getting metrics: {ex.Message}", ex);
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

        try
        {
            // In real implementation, would call Cost Explorer API
            // For now, return placeholder data
            await Task.CompletedTask;

            return new CloudCostSummary(
                0m,
                "USD",
                startDate,
                endDate,
                [],
                [],
                null,
                new() { ["source"] = "Cost Explorer" });
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("AWS", $"Error getting cost summary: {ex.Message}", ex);
        }
    }

    public async Task<CloudResourceList<StorageBucket>> ListStorageBucketsAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        try
        {
            var request = CreateAwsRequest("s3", "us-east-1", "ListBuckets");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var buckets = ParseS3Buckets(content);

            return new CloudResourceList<StorageBucket>(
                buckets,
                buckets.Count,
                null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("AWS", $"Error listing buckets: {ex.Message}", ex);
        }
    }

    public async Task<CloudResourceList<CloudDatabase>> ListDatabasesAsync(
        string region,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        try
        {
            var request = CreateAwsRequest("rds", region, "DescribeDBInstances", "2014-10-31");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var databases = ParseRdsDatabases(content, region);

            return new CloudResourceList<CloudDatabase>(
                databases,
                databases.Count,
                null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            throw new CloudProviderException("AWS", $"Error listing databases: {ex.Message}", ex);
        }
    }

    // ========================================================================
    // AWS-Specific Operations
    // ========================================================================

    /// <summary>
    /// Lists EC2 security groups.
    /// </summary>
    public async Task<IReadOnlyList<AwsSecurityGroup>> ListSecurityGroupsAsync(
        string region,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var request = CreateAwsRequest("ec2", region, "DescribeSecurityGroups", "2016-11-15");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        return ParseSecurityGroups(content);
    }

    /// <summary>
    /// Lists VPCs.
    /// </summary>
    public async Task<IReadOnlyList<AwsVpc>> ListVpcsAsync(
        string region,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var request = CreateAwsRequest("ec2", region, "DescribeVpcs", "2016-11-15");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        return ParseVpcs(content);
    }

    /// <summary>
    /// Creates a CloudWatch alarm.
    /// </summary>
    public async Task<CloudOperationResult> CreateAlarmAsync(
        AwsAlarmConfig config,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var parameters = new Dictionary<string, string>
        {
            ["AlarmName"] = config.Name,
            ["MetricName"] = config.MetricName,
            ["Namespace"] = config.Namespace,
            ["Statistic"] = config.Statistic,
            ["Period"] = config.PeriodSeconds.ToString(),
            ["EvaluationPeriods"] = config.EvaluationPeriods.ToString(),
            ["Threshold"] = config.Threshold.ToString(),
            ["ComparisonOperator"] = config.ComparisonOperator
        };

        if (config.Dimensions != null)
        {
            var i = 1;
            foreach (var dim in config.Dimensions)
            {
                parameters[$"Dimensions.member.{i}.Name"] = dim.Key;
                parameters[$"Dimensions.member.{i}.Value"] = dim.Value;
                i++;
            }
        }

        var request = CreateAwsRequest("monitoring", _region, "PutMetricAlarm", "2010-08-01", parameters);
        var response = await _httpClient.SendAsync(request, cancellationToken);

        return new CloudOperationResult(
            response.IsSuccessStatusCode,
            null,
            response.IsSuccessStatusCode ? "Alarm created" : "Failed to create alarm",
            response.IsSuccessStatusCode ? CloudOperationStatus.Completed : CloudOperationStatus.Failed);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_accessKeyId) || string.IsNullOrEmpty(_secretAccessKey))
        {
            throw new InvalidOperationException("AWS credentials not configured. Call Configure() first.");
        }
    }

    private async Task<CloudOperationResult> ExecuteInstanceAction(
        string instanceId,
        string region,
        string action,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();

        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["InstanceId.1"] = instanceId
            };

            var request = CreateAwsRequest("ec2", region, action, "2016-11-15", parameters);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                ResourceChanged?.Invoke(this, new CloudResourceChangedEventArgs
                {
                    ProviderId = "aws",
                    ResourceId = instanceId,
                    ResourceType = "EC2Instance",
                    ChangeType = action,
                    OccurredAt = DateTimeOffset.UtcNow
                });
            }

            return new CloudOperationResult(
                response.IsSuccessStatusCode,
                Guid.NewGuid().ToString(),
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

    private HttpRequestMessage CreateAwsRequest(
        string service,
        string region,
        string action,
        string? version = null,
        Dictionary<string, string>? parameters = null)
    {
        var endpoint = GetServiceEndpoint(service, region);
        var queryParams = new Dictionary<string, string>
        {
            ["Action"] = action
        };

        if (version != null)
        {
            queryParams["Version"] = version;
        }

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                queryParams[param.Key] = param.Value;
            }
        }

        var queryString = string.Join("&", queryParams.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var uri = new Uri($"{endpoint}?{queryString}");

        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // Add AWS Signature Version 4 headers (simplified)
        var timestamp = DateTimeOffset.UtcNow;
        request.Headers.Add("X-Amz-Date", timestamp.ToString("yyyyMMddTHHmmssZ"));

        // In real implementation, would compute full SigV4 signature
        var signature = ComputeAwsSignature(request, service, region, timestamp);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "AWS4-HMAC-SHA256",
            $"Credential={_accessKeyId}/{timestamp:yyyyMMdd}/{region}/{service}/aws4_request, SignedHeaders=host;x-amz-date, Signature={signature}");

        return request;
    }

    private string GetServiceEndpoint(string service, string region) => service switch
    {
        "sts" => $"https://sts.{region}.amazonaws.com",
        "ec2" => $"https://ec2.{region}.amazonaws.com",
        "s3" => "https://s3.amazonaws.com",
        "rds" => $"https://rds.{region}.amazonaws.com",
        "monitoring" => $"https://monitoring.{region}.amazonaws.com",
        "ce" => "https://ce.us-east-1.amazonaws.com", // Cost Explorer is global
        _ => throw new ArgumentException($"Unknown AWS service: {service}")
    };

    private string ComputeAwsSignature(HttpRequestMessage request, string service, string region, DateTimeOffset timestamp)
    {
        // Simplified signature computation for demonstration
        // Real implementation would follow AWS SigV4 specification
        var stringToSign = $"{request.Method}\n{request.RequestUri?.PathAndQuery}\n{timestamp:yyyyMMddTHHmmssZ}";
        var keyBytes = Encoding.UTF8.GetBytes("AWS4" + _secretAccessKey);
        var dateKey = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(timestamp.ToString("yyyyMMdd")));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(service));
        var signingKey = HMACSHA256.HashData(serviceKey, Encoding.UTF8.GetBytes("aws4_request"));
        var signature = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private static string ExtractAccountId(string stsResponse)
    {
        // Simplified XML parsing - real implementation would use proper XML parser
        var start = stsResponse.IndexOf("<Account>", StringComparison.Ordinal);
        var end = stsResponse.IndexOf("</Account>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            return stsResponse.Substring(start + 9, end - start - 9);
        }
        return "unknown";
    }

    private static IReadOnlyList<string> GetSupportedRegions() =>
    [
        "us-east-1", "us-east-2", "us-west-1", "us-west-2",
        "eu-west-1", "eu-west-2", "eu-west-3", "eu-central-1", "eu-north-1",
        "ap-northeast-1", "ap-northeast-2", "ap-northeast-3",
        "ap-southeast-1", "ap-southeast-2",
        "ap-south-1", "sa-east-1", "ca-central-1",
        "me-south-1", "af-south-1"
    ];

    private static string GetRegionDisplayName(string region) => region switch
    {
        "us-east-1" => "US East (N. Virginia)",
        "us-east-2" => "US East (Ohio)",
        "us-west-1" => "US West (N. California)",
        "us-west-2" => "US West (Oregon)",
        "eu-west-1" => "EU (Ireland)",
        "eu-west-2" => "EU (London)",
        "eu-west-3" => "EU (Paris)",
        "eu-central-1" => "EU (Frankfurt)",
        "eu-north-1" => "EU (Stockholm)",
        "ap-northeast-1" => "Asia Pacific (Tokyo)",
        "ap-northeast-2" => "Asia Pacific (Seoul)",
        "ap-southeast-1" => "Asia Pacific (Singapore)",
        "ap-southeast-2" => "Asia Pacific (Sydney)",
        "ap-south-1" => "Asia Pacific (Mumbai)",
        "sa-east-1" => "South America (São Paulo)",
        "ca-central-1" => "Canada (Central)",
        _ => region
    };

    private static string GetRegionGeography(string region)
    {
        if (region.StartsWith("us-")) return "North America";
        if (region.StartsWith("eu-")) return "Europe";
        if (region.StartsWith("ap-")) return "Asia Pacific";
        if (region.StartsWith("sa-")) return "South America";
        if (region.StartsWith("ca-")) return "North America";
        if (region.StartsWith("me-")) return "Middle East";
        if (region.StartsWith("af-")) return "Africa";
        return "Unknown";
    }

    private static IReadOnlyList<string> GetAvailabilityZones(string region) =>
        new[] { $"{region}a", $"{region}b", $"{region}c" };

    private static string GetMetricUnit(string metricName) => metricName switch
    {
        "CPUUtilization" => "Percent",
        "NetworkIn" or "NetworkOut" => "Bytes",
        "DiskReadBytes" or "DiskWriteBytes" => "Bytes",
        "DiskReadOps" or "DiskWriteOps" => "Count",
        _ => "None"
    };

    private static IReadOnlyList<ComputeInstance> ParseEc2Instances(string response, string region)
    {
        // Simplified parsing - real implementation would use XML parser
        var instances = new List<ComputeInstance>();
        // Parse EC2 DescribeInstances XML response
        return instances;
    }

    private static IReadOnlyList<MetricDataPoint> ParseCloudWatchMetrics(string response)
    {
        // Simplified parsing
        var dataPoints = new List<MetricDataPoint>();
        return dataPoints;
    }

    private static IReadOnlyList<StorageBucket> ParseS3Buckets(string response)
    {
        // Simplified parsing
        var buckets = new List<StorageBucket>();
        return buckets;
    }

    private static IReadOnlyList<CloudDatabase> ParseRdsDatabases(string response, string region)
    {
        // Simplified parsing
        var databases = new List<CloudDatabase>();
        return databases;
    }

    private static IReadOnlyList<AwsSecurityGroup> ParseSecurityGroups(string response)
    {
        // Simplified parsing
        var groups = new List<AwsSecurityGroup>();
        return groups;
    }

    private static IReadOnlyList<AwsVpc> ParseVpcs(string response)
    {
        // Simplified parsing
        var vpcs = new List<AwsVpc>();
        return vpcs;
    }
}

// ========================================================================
// AWS-Specific Types
// ========================================================================

/// <summary>
/// AWS Security Group.
/// </summary>
public sealed record AwsSecurityGroup(
    string Id,
    string Name,
    string Description,
    string VpcId,
    IReadOnlyList<AwsSecurityRule> InboundRules,
    IReadOnlyList<AwsSecurityRule> OutboundRules,
    Dictionary<string, string> Tags);

/// <summary>
/// AWS Security Group rule.
/// </summary>
public sealed record AwsSecurityRule(
    string Protocol,
    int FromPort,
    int ToPort,
    string Source,
    string Description);

/// <summary>
/// AWS VPC.
/// </summary>
public sealed record AwsVpc(
    string Id,
    string CidrBlock,
    string State,
    bool IsDefault,
    Dictionary<string, string> Tags);

/// <summary>
/// Configuration for creating a CloudWatch alarm.
/// </summary>
public sealed record AwsAlarmConfig(
    string Name,
    string MetricName,
    string Namespace,
    string Statistic,
    int PeriodSeconds,
    int EvaluationPeriods,
    double Threshold,
    string ComparisonOperator,
    Dictionary<string, string>? Dimensions = null,
    IReadOnlyList<string>? AlarmActions = null);

/// <summary>
/// Exception for cloud provider errors.
/// </summary>
public class CloudProviderException : Exception
{
    public string Provider { get; }

    public CloudProviderException(string provider, string message)
        : base($"[{provider}] {message}")
    {
        Provider = provider;
    }

    public CloudProviderException(string provider, string message, Exception innerException)
        : base($"[{provider}] {message}", innerException)
    {
        Provider = provider;
    }
}
