using ControlRoom.Application.Integrations;
using ControlRoom.Domain.Model;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Tests for cloud provider integrations (AWS, Azure, GCP).
/// </summary>
public sealed class CloudProviderTests
{
    // ========================================================================
    // Interface Contract Tests
    // ========================================================================

    [Fact]
    public void ICloudProvider_Interface_DefinesAllRequiredMethods()
    {
        // Verify interface has all expected methods
        var interfaceType = typeof(ICloudProvider);

        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "ValidateCredentialsAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "ListInstancesAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "GetInstanceAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "StartInstanceAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "StopInstanceAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "RestartInstanceAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "GetMetricsAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "ListRegionsAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "GetCostSummaryAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "ListStorageBucketsAsync");
        Assert.Contains(interfaceType.GetMethods(), m => m.Name == "ListDatabasesAsync");
    }

    // ========================================================================
    // CloudValidationResult Tests
    // ========================================================================

    [Fact]
    public void CloudValidationResult_ValidCredentials_HasCorrectProperties()
    {
        var result = new CloudValidationResult(
            true,
            null,
            "123456789",
            "Test Account",
            ["us-east-1", "eu-west-1"],
            new() { ["key"] = "value" });

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("123456789", result.AccountId);
        Assert.Equal("Test Account", result.AccountName);
        Assert.Equal(2, result.AvailableRegions.Count);
    }

    [Fact]
    public void CloudValidationResult_InvalidCredentials_HasErrorMessage()
    {
        var result = new CloudValidationResult(
            false,
            "Invalid access key",
            null,
            null,
            []);

        Assert.False(result.IsValid);
        Assert.Equal("Invalid access key", result.ErrorMessage);
        Assert.Null(result.AccountId);
    }

    // ========================================================================
    // CloudResourceList Tests
    // ========================================================================

    [Fact]
    public void CloudResourceList_WithItems_HasCorrectCounts()
    {
        var instances = new List<ComputeInstance>
        {
            CreateTestInstance("i-1", "instance-1"),
            CreateTestInstance("i-2", "instance-2")
        };

        var list = new CloudResourceList<ComputeInstance>(
            instances,
            2,
            null,
            DateTimeOffset.UtcNow);

        Assert.Equal(2, list.Items.Count);
        Assert.Equal(2, list.TotalCount);
        Assert.Null(list.NextPageToken);
    }

    [Fact]
    public void CloudResourceList_WithPagination_HasNextToken()
    {
        var list = new CloudResourceList<ComputeInstance>(
            [CreateTestInstance("i-1", "instance-1")],
            100,
            "next-page-token",
            DateTimeOffset.UtcNow);

        Assert.Single(list.Items);
        Assert.Equal(100, list.TotalCount);
        Assert.Equal("next-page-token", list.NextPageToken);
    }

    // ========================================================================
    // CloudOperationResult Tests
    // ========================================================================

    [Fact]
    public void CloudOperationResult_Success_HasCorrectStatus()
    {
        var result = new CloudOperationResult(
            true,
            "op-12345",
            "Instance started successfully",
            CloudOperationStatus.Completed);

        Assert.True(result.Success);
        Assert.Equal("op-12345", result.OperationId);
        Assert.Equal(CloudOperationStatus.Completed, result.Status);
    }

    [Fact]
    public void CloudOperationResult_InProgress_HasPendingStatus()
    {
        var result = new CloudOperationResult(
            true,
            "op-12345",
            "Starting instance",
            CloudOperationStatus.InProgress);

        Assert.True(result.Success);
        Assert.Equal(CloudOperationStatus.InProgress, result.Status);
    }

    [Fact]
    public void CloudOperationResult_Failure_HasFailedStatus()
    {
        var result = new CloudOperationResult(
            false,
            null,
            "Instance not found",
            CloudOperationStatus.Failed);

        Assert.False(result.Success);
        Assert.Null(result.OperationId);
        Assert.Equal(CloudOperationStatus.Failed, result.Status);
    }

    // ========================================================================
    // ComputeInstance Tests
    // ========================================================================

    [Fact]
    public void ComputeInstance_HasAllRequiredProperties()
    {
        var instance = new ComputeInstance(
            "i-1234567890abcdef0",
            "my-instance",
            "t3.medium",
            ComputeInstanceState.Running,
            "us-east-1",
            "us-east-1a",
            "10.0.1.100",
            "54.123.45.67",
            "vpc-12345",
            "subnet-12345",
            "ami-12345",
            "Linux",
            2,
            4096,
            DateTimeOffset.UtcNow.AddDays(-30),
            new() { ["Name"] = "my-instance", ["Environment"] = "production" },
            new() { ["extra"] = "data" });

        Assert.Equal("i-1234567890abcdef0", instance.Id);
        Assert.Equal("my-instance", instance.Name);
        Assert.Equal("t3.medium", instance.InstanceType);
        Assert.Equal(ComputeInstanceState.Running, instance.State);
        Assert.Equal("us-east-1", instance.Region);
        Assert.Equal("us-east-1a", instance.AvailabilityZone);
        Assert.Equal("10.0.1.100", instance.PrivateIp);
        Assert.Equal("54.123.45.67", instance.PublicIp);
        Assert.Equal(2, instance.CpuCores);
        Assert.Equal(4096, instance.MemoryMb);
        Assert.Equal(2, instance.Tags.Count);
    }

    [Theory]
    [InlineData(ComputeInstanceState.Running)]
    [InlineData(ComputeInstanceState.Stopped)]
    [InlineData(ComputeInstanceState.Pending)]
    [InlineData(ComputeInstanceState.Stopping)]
    [InlineData(ComputeInstanceState.Terminated)]
    public void ComputeInstanceState_HasAllExpectedValues(ComputeInstanceState state)
    {
        Assert.True(Enum.IsDefined(typeof(ComputeInstanceState), state));
    }

    // ========================================================================
    // CloudRegion Tests
    // ========================================================================

    [Fact]
    public void CloudRegion_HasCorrectProperties()
    {
        var region = new CloudRegion(
            "us-east-1",
            "us-east-1",
            "US East (N. Virginia)",
            "North America",
            true,
            ["us-east-1a", "us-east-1b", "us-east-1c"]);

        Assert.Equal("us-east-1", region.Id);
        Assert.Equal("US East (N. Virginia)", region.DisplayName);
        Assert.Equal("North America", region.GeographicLocation);
        Assert.True(region.IsEnabled);
        Assert.Equal(3, region.AvailabilityZones.Count);
    }

    // ========================================================================
    // CloudMetrics Tests
    // ========================================================================

    [Fact]
    public void CloudMetrics_WithDataPoints_HasCorrectStructure()
    {
        var dataPoints = new List<MetricDataPoint>
        {
            new(DateTimeOffset.UtcNow.AddMinutes(-10), 45.5, 10.0, 90.0, 455.0, 10),
            new(DateTimeOffset.UtcNow.AddMinutes(-5), 50.0, 15.0, 85.0, 500.0, 10),
            new(DateTimeOffset.UtcNow, 55.5, 20.0, 80.0, 555.0, 10)
        };

        var startTime = DateTimeOffset.UtcNow.AddMinutes(-15);
        var endTime = DateTimeOffset.UtcNow;

        var metrics = new CloudMetrics(
            "i-12345",
            "CPUUtilization",
            "Percent",
            dataPoints,
            startTime,
            endTime);

        Assert.Equal("i-12345", metrics.ResourceId);
        Assert.Equal("CPUUtilization", metrics.MetricName);
        Assert.Equal("Percent", metrics.Unit);
        Assert.Equal(3, metrics.DataPoints.Count);
        Assert.Equal(startTime, metrics.StartTime);
        Assert.Equal(endTime, metrics.EndTime);
    }

    [Fact]
    public void MetricDataPoint_HasAllStatistics()
    {
        var dataPoint = new MetricDataPoint(
            DateTimeOffset.UtcNow,
            45.5,  // Average
            10.0,  // Minimum
            90.0,  // Maximum
            455.0, // Sum
            10);   // SampleCount

        Assert.Equal(45.5, dataPoint.Average);
        Assert.Equal(10.0, dataPoint.Minimum);
        Assert.Equal(90.0, dataPoint.Maximum);
        Assert.Equal(455.0, dataPoint.Sum);
        Assert.Equal(10, dataPoint.SampleCount);
    }

    // ========================================================================
    // CloudCostSummary Tests
    // ========================================================================

    [Fact]
    public void CloudCostSummary_HasCorrectTotals()
    {
        var costsByService = new List<CostByService>
        {
            new("EC2", 500m, 50m),
            new("S3", 200m, 20m),
            new("RDS", 300m, 30m)
        };

        var costsByRegion = new List<CostByRegion>
        {
            new("us-east-1", 700m, 70m),
            new("eu-west-1", 300m, 30m)
        };

        var summary = new CloudCostSummary(
            1000m,
            "USD",
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow,
            costsByService,
            costsByRegion,
            1100m);

        Assert.Equal(1000m, summary.TotalCost);
        Assert.Equal("USD", summary.Currency);
        Assert.Equal(3, summary.CostsByService.Count);
        Assert.Equal(2, summary.CostsByRegion.Count);
        Assert.Equal(1100m, summary.ForecastedCost);
    }

    // ========================================================================
    // StorageBucket Tests
    // ========================================================================

    [Fact]
    public void StorageBucket_HasCorrectProperties()
    {
        var bucket = new StorageBucket(
            "my-bucket",
            "us-east-1",
            DateTimeOffset.UtcNow.AddYears(-1),
            1024L * 1024 * 1024 * 100, // 100 GB
            10000,
            StorageClass.Standard,
            false,
            true,
            new() { ["Environment"] = "production" });

        Assert.Equal("my-bucket", bucket.Name);
        Assert.Equal("us-east-1", bucket.Region);
        Assert.Equal(100L * 1024 * 1024 * 1024, bucket.SizeBytes);
        Assert.Equal(10000, bucket.ObjectCount);
        Assert.Equal(StorageClass.Standard, bucket.StorageClass);
        Assert.False(bucket.IsPublic);
        Assert.True(bucket.VersioningEnabled);
    }

    [Theory]
    [InlineData(StorageClass.Standard)]
    [InlineData(StorageClass.InfrequentAccess)]
    [InlineData(StorageClass.Archive)]
    [InlineData(StorageClass.Glacier)]
    [InlineData(StorageClass.ColdLine)]
    [InlineData(StorageClass.HotTier)]
    [InlineData(StorageClass.CoolTier)]
    public void StorageClass_HasAllExpectedValues(StorageClass storageClass)
    {
        Assert.True(Enum.IsDefined(typeof(StorageClass), storageClass));
    }

    // ========================================================================
    // CloudDatabase Tests
    // ========================================================================

    [Fact]
    public void CloudDatabase_HasCorrectProperties()
    {
        var database = new CloudDatabase(
            "db-12345",
            "my-database",
            "PostgreSQL",
            "14.5",
            "db.r5.large",
            CloudDatabaseState.Available,
            "us-east-1",
            "us-east-1a",
            100,
            true,
            "my-database.cluster-12345.us-east-1.rds.amazonaws.com",
            5432,
            DateTimeOffset.UtcNow.AddMonths(-6),
            new() { ["Environment"] = "production" });

        Assert.Equal("db-12345", database.Id);
        Assert.Equal("my-database", database.Name);
        Assert.Equal("PostgreSQL", database.Engine);
        Assert.Equal("14.5", database.EngineVersion);
        Assert.Equal(CloudDatabaseState.Available, database.State);
        Assert.Equal(100, database.StorageGb);
        Assert.True(database.MultiAz);
        Assert.Equal(5432, database.Port);
    }

    [Theory]
    [InlineData(CloudDatabaseState.Available)]
    [InlineData(CloudDatabaseState.Creating)]
    [InlineData(CloudDatabaseState.Deleting)]
    [InlineData(CloudDatabaseState.Failed)]
    [InlineData(CloudDatabaseState.Maintenance)]
    [InlineData(CloudDatabaseState.Stopped)]
    public void CloudDatabaseState_HasAllExpectedValues(CloudDatabaseState state)
    {
        Assert.True(Enum.IsDefined(typeof(CloudDatabaseState), state));
    }

    // ========================================================================
    // AWS Provider Tests
    // ========================================================================

    [Fact]
    public void AwsProvider_ProviderName_IsCorrect()
    {
        var provider = new AwsProvider();
        Assert.Equal("aws", provider.ProviderName);
    }

    [Fact]
    public async Task AwsProvider_ValidateCredentials_FailsWithoutConfiguration()
    {
        var provider = new AwsProvider();

        var result = await provider.ValidateCredentialsAsync(new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains("Missing required credentials", result.ErrorMessage);
    }

    [Fact]
    public void AwsSecurityGroup_HasCorrectStructure()
    {
        var inboundRules = new List<AwsSecurityRule>
        {
            new("tcp", 443, 443, "0.0.0.0/0", "HTTPS"),
            new("tcp", 22, 22, "10.0.0.0/8", "SSH from internal")
        };

        var sg = new AwsSecurityGroup(
            "sg-12345",
            "web-server-sg",
            "Security group for web servers",
            "vpc-12345",
            inboundRules,
            [],
            new() { ["Name"] = "web-server-sg" });

        Assert.Equal("sg-12345", sg.Id);
        Assert.Equal(2, sg.InboundRules.Count);
        Assert.Equal(443, sg.InboundRules[0].FromPort);
    }

    [Fact]
    public void AwsAlarmConfig_HasAllRequiredFields()
    {
        var config = new AwsAlarmConfig(
            "high-cpu-alarm",
            "CPUUtilization",
            "AWS/EC2",
            "Average",
            300,
            2,
            80.0,
            "GreaterThanThreshold",
            new() { ["InstanceId"] = "i-12345" });

        Assert.Equal("high-cpu-alarm", config.Name);
        Assert.Equal("CPUUtilization", config.MetricName);
        Assert.Equal(80.0, config.Threshold);
        Assert.Equal("GreaterThanThreshold", config.ComparisonOperator);
    }

    // ========================================================================
    // Azure Provider Tests
    // ========================================================================

    [Fact]
    public void AzureProvider_ProviderName_IsCorrect()
    {
        var provider = new AzureProvider();
        Assert.Equal("azure", provider.ProviderName);
    }

    [Fact]
    public async Task AzureProvider_ValidateCredentials_FailsWithoutConfiguration()
    {
        var provider = new AzureProvider();

        var result = await provider.ValidateCredentialsAsync(new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains("Missing required credentials", result.ErrorMessage);
    }

    [Fact]
    public void AzureResourceGroup_HasCorrectStructure()
    {
        var rg = new AzureResourceGroup
        {
            Id = "/subscriptions/sub-123/resourceGroups/my-rg",
            Name = "my-rg",
            Location = "eastus",
            Tags = new() { ["Environment"] = "production" },
            Properties = new AzureResourceGroupProperties { ProvisioningState = "Succeeded" }
        };

        Assert.Equal("my-rg", rg.Name);
        Assert.Equal("eastus", rg.Location);
        Assert.Equal("Succeeded", rg.Properties?.ProvisioningState);
    }

    [Fact]
    public void AzureAlertRuleConfig_HasAllRequiredFields()
    {
        var config = new AzureAlertRuleConfig(
            "high-cpu-alert",
            "High CPU utilization detected",
            2,
            ["/subscriptions/sub-123/resourceGroups/my-rg"],
            "Percentage CPU",
            "Microsoft.Compute/virtualMachines",
            "GreaterThan",
            80.0,
            "Average");

        Assert.Equal("high-cpu-alert", config.Name);
        Assert.Equal(2, config.Severity);
        Assert.Equal(80.0, config.Threshold);
    }

    // ========================================================================
    // GCP Provider Tests
    // ========================================================================

    [Fact]
    public void GcpProvider_ProviderName_IsCorrect()
    {
        var provider = new GcpProvider();
        Assert.Equal("gcp", provider.ProviderName);
    }

    [Fact]
    public async Task GcpProvider_ValidateCredentials_FailsWithoutConfiguration()
    {
        var provider = new GcpProvider();

        var result = await provider.ValidateCredentialsAsync(new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains("Missing required credentials", result.ErrorMessage);
    }

    [Fact]
    public void GcpZone_HasCorrectStructure()
    {
        var zone = new GcpZone
        {
            Name = "us-central1-a",
            Description = "us-central1-a",
            Region = "https://www.googleapis.com/compute/v1/projects/my-project/regions/us-central1",
            Status = "UP"
        };

        Assert.Equal("us-central1-a", zone.Name);
        Assert.Equal("UP", zone.Status);
    }

    [Fact]
    public void GcpFirewallRule_HasCorrectStructure()
    {
        var rule = new GcpFirewallRule
        {
            Name = "allow-https",
            Network = "https://www.googleapis.com/compute/v1/projects/my-project/global/networks/default",
            Direction = "INGRESS",
            Priority = 1000,
            SourceRanges = ["0.0.0.0/0"],
            Allowed = [new GcpFirewallAllowed { IpProtocol = "tcp", Ports = ["443"] }]
        };

        Assert.Equal("allow-https", rule.Name);
        Assert.Equal("INGRESS", rule.Direction);
        Assert.Single(rule.SourceRanges!);
        Assert.Single(rule.Allowed!);
    }

    [Fact]
    public void GcpAlertPolicyConfig_HasAllRequiredFields()
    {
        var config = new GcpAlertPolicyConfig(
            "High CPU Alert",
            "CPU Utilization > 80%",
            "metric.type=\"compute.googleapis.com/instance/cpu/utilization\"",
            "COMPARISON_GT",
            0.8);

        Assert.Equal("High CPU Alert", config.DisplayName);
        Assert.Equal(0.8, config.Threshold);
        Assert.Equal("COMPARISON_GT", config.Comparison);
    }

    // ========================================================================
    // CloudProviderException Tests
    // ========================================================================

    [Fact]
    public void CloudProviderException_HasProviderInfo()
    {
        var exception = new CloudProviderException("aws", "Failed to list instances");

        Assert.Equal("aws", exception.Provider);
        Assert.Contains("[aws]", exception.Message);
        Assert.Contains("Failed to list instances", exception.Message);
    }

    [Fact]
    public void CloudProviderException_WithInnerException_PreservesChain()
    {
        var inner = new InvalidOperationException("Network error");
        var exception = new CloudProviderException("azure", "API call failed", inner);

        Assert.Equal("azure", exception.Provider);
        Assert.Same(inner, exception.InnerException);
    }

    // ========================================================================
    // Multi-Cloud Types Tests
    // ========================================================================

    [Fact]
    public void MultiCloudResourceList_AggregatesFromMultipleProviders()
    {
        var items = new List<ProviderResource<ComputeInstance>>
        {
            new("aws", new IntegrationInstanceId(Guid.NewGuid()), CreateTestInstance("i-1", "aws-instance")),
            new("azure", new IntegrationInstanceId(Guid.NewGuid()), CreateTestInstance("vm-1", "azure-vm")),
            new("gcp", new IntegrationInstanceId(Guid.NewGuid()), CreateTestInstance("inst-1", "gcp-instance"))
        };

        var list = new MultiCloudResourceList<ComputeInstance>(
            items,
            3,
            new(),
            DateTimeOffset.UtcNow);

        Assert.Equal(3, list.Items.Count);
        Assert.Contains(list.Items, i => i.ProviderName == "aws");
        Assert.Contains(list.Items, i => i.ProviderName == "azure");
        Assert.Contains(list.Items, i => i.ProviderName == "gcp");
    }

    [Fact]
    public void MultiCloudCostSummary_AggregatesProviderCosts()
    {
        var providerCosts = new List<ProviderCostSummary>
        {
            new("aws", new IntegrationInstanceId(Guid.NewGuid()),
                new CloudCostSummary(500m, "USD", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, [], [], null)),
            new("azure", new IntegrationInstanceId(Guid.NewGuid()),
                new CloudCostSummary(300m, "USD", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, [], [], null))
        };

        var summary = new MultiCloudCostSummary(
            800m,
            "USD",
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow,
            providerCosts,
            new(),
            DateTimeOffset.UtcNow);

        Assert.Equal(800m, summary.TotalCost);
        Assert.Equal(2, summary.ProviderCosts.Count);
    }

    [Fact]
    public void ProviderHealthStatus_HasAllRequiredInfo()
    {
        var status = new ProviderHealthStatus(
            "aws",
            new IntegrationInstanceId(Guid.NewGuid()),
            IntegrationHealth.Healthy,
            TimeSpan.FromMilliseconds(150),
            null,
            DateTimeOffset.UtcNow);

        Assert.Equal("aws", status.ProviderName);
        Assert.Equal(IntegrationHealth.Healthy, status.Health);
        Assert.Equal(150, status.ResponseTime.TotalMilliseconds);
    }

    [Theory]
    [InlineData(CloudInstanceAction.Start)]
    [InlineData(CloudInstanceAction.Stop)]
    [InlineData(CloudInstanceAction.Restart)]
    public void CloudInstanceAction_HasAllExpectedValues(CloudInstanceAction action)
    {
        Assert.True(Enum.IsDefined(typeof(CloudInstanceAction), action));
    }

    // ========================================================================
    // Event Args Tests
    // ========================================================================

    [Fact]
    public void CloudResourceChangedEventArgs_HasAllRequiredProperties()
    {
        var args = new CloudResourceChangedEventArgs
        {
            ProviderId = "aws",
            ResourceId = "i-12345",
            ResourceType = "EC2Instance",
            ChangeType = "start",
            OccurredAt = DateTimeOffset.UtcNow,
            Details = new() { ["previousState"] = "stopped" }
        };

        Assert.Equal("aws", args.ProviderId);
        Assert.Equal("i-12345", args.ResourceId);
        Assert.Equal("EC2Instance", args.ResourceType);
        Assert.Equal("start", args.ChangeType);
    }

    [Fact]
    public void CloudAlertEventArgs_HasAllRequiredProperties()
    {
        var args = new CloudAlertEventArgs
        {
            ProviderId = "azure",
            AlertId = "alert-123",
            AlertName = "High CPU",
            Severity = CloudAlertSeverity.Warning,
            Message = "CPU utilization exceeded 80%",
            ResourceId = "/subscriptions/sub-123/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
            TriggeredAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("azure", args.ProviderId);
        Assert.Equal("High CPU", args.AlertName);
        Assert.Equal(CloudAlertSeverity.Warning, args.Severity);
    }

    [Theory]
    [InlineData(CloudAlertSeverity.Info)]
    [InlineData(CloudAlertSeverity.Warning)]
    [InlineData(CloudAlertSeverity.Error)]
    [InlineData(CloudAlertSeverity.Critical)]
    public void CloudAlertSeverity_HasAllExpectedValues(CloudAlertSeverity severity)
    {
        Assert.True(Enum.IsDefined(typeof(CloudAlertSeverity), severity));
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private static ComputeInstance CreateTestInstance(string id, string name)
    {
        return new ComputeInstance(
            id,
            name,
            "t3.medium",
            ComputeInstanceState.Running,
            "us-east-1",
            "us-east-1a",
            "10.0.1.100",
            null,
            null,
            null,
            null,
            null,
            2,
            4096,
            DateTimeOffset.UtcNow,
            new());
    }
}
