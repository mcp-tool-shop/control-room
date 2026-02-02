using ControlRoom.Application.Services;
using ControlRoom.Domain.Model;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Unit tests for HealthCheckService components.
/// </summary>
public sealed class HealthCheckServiceTests
{
    [Fact]
    public void HealthCheck_BasicConstruction()
    {
        // Arrange & Act
        var check = new HealthCheck(
            HealthCheckId.New(),
            "Test Check",
            "A test health check",
            HealthCheckType.Http,
            new Dictionary<string, string> { ["url"] = "https://example.com" },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(30),
            true
        );

        // Assert
        Assert.Equal("Test Check", check.Name);
        Assert.Equal(HealthCheckType.Http, check.Type);
        Assert.True(check.IsEnabled);
    }

    [Fact]
    public void HealthCheckResult_BasicConstruction()
    {
        // Arrange & Act
        var result = new HealthCheckResult(
            HealthCheckId.New(),
            "Test Check",
            HealthStatus.Healthy,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(150),
            "HTTP 200 OK",
            new Dictionary<string, object> { ["status_code"] = 200 }
        );

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("HTTP 200 OK", result.Message);
        Assert.NotNull(result.Details);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, "Healthy")]
    [InlineData(HealthStatus.Degraded, "Degraded")]
    [InlineData(HealthStatus.Unhealthy, "Unhealthy")]
    [InlineData(HealthStatus.Unknown, "Unknown")]
    public void HealthStatus_AllValues(HealthStatus status, string expected)
    {
        Assert.Equal(expected, status.ToString());
    }

    [Theory]
    [InlineData(HealthCheckType.Http, "Http")]
    [InlineData(HealthCheckType.Tcp, "Tcp")]
    [InlineData(HealthCheckType.Dns, "Dns")]
    [InlineData(HealthCheckType.Ping, "Ping")]
    [InlineData(HealthCheckType.Script, "Script")]
    [InlineData(HealthCheckType.Database, "Database")]
    [InlineData(HealthCheckType.Service, "Service")]
    public void HealthCheckType_AllValues(HealthCheckType type, string expected)
    {
        Assert.Equal(expected, type.ToString());
    }

    [Fact]
    public void HealthCheckBuilder_HttpCheck()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("API Health")
            .WithDescription("Check API endpoint")
            .HttpCheck("https://api.example.com/health")
            .WithInterval(TimeSpan.FromMinutes(1))
            .Build();

        // Assert
        Assert.Equal("API Health", check.Name);
        Assert.Equal(HealthCheckType.Http, check.Type);
        Assert.Equal("https://api.example.com/health", check.Config["url"]);
    }

    [Fact]
    public void HealthCheckBuilder_HttpCheckWithMethod()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("API Health")
            .HttpCheck("https://api.example.com/health", "HEAD", 204)
            .Build();

        // Assert
        Assert.Equal("HEAD", check.Config["method"]);
        Assert.Equal("204", check.Config["expected_status"]);
    }

    [Fact]
    public void HealthCheckBuilder_TcpCheck()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("Database Port")
            .TcpCheck("localhost", 5432)
            .Build();

        // Assert
        Assert.Equal(HealthCheckType.Tcp, check.Type);
        Assert.Equal("localhost", check.Config["host"]);
        Assert.Equal("5432", check.Config["port"]);
    }

    [Fact]
    public void HealthCheckBuilder_DnsCheck()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("DNS Resolution")
            .DnsCheck("google.com")
            .Build();

        // Assert
        Assert.Equal(HealthCheckType.Dns, check.Type);
        Assert.Equal("google.com", check.Config["hostname"]);
    }

    [Fact]
    public void HealthCheckBuilder_PingCheck()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("Network Ping")
            .PingCheck("8.8.8.8", 5000)
            .Build();

        // Assert
        Assert.Equal(HealthCheckType.Ping, check.Type);
        Assert.Equal("8.8.8.8", check.Config["host"]);
        Assert.Equal("5000", check.Config["timeout_ms"]);
    }

    [Fact]
    public void HealthCheckBuilder_ScriptCheck()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("Custom Check")
            .ScriptCheck("C:\\scripts\\healthcheck.ps1", "powershell", "-Verbose")
            .Build();

        // Assert
        Assert.Equal(HealthCheckType.Script, check.Type);
        Assert.Equal("C:\\scripts\\healthcheck.ps1", check.Config["script_path"]);
        Assert.Equal("powershell", check.Config["interpreter"]);
        Assert.Equal("-Verbose", check.Config["arguments"]);
    }

    [Fact]
    public void HealthCheckBuilder_DatabaseCheck()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("SQLite DB")
            .DatabaseCheck("Data Source=test.db", "sqlite")
            .Build();

        // Assert
        Assert.Equal(HealthCheckType.Database, check.Type);
        Assert.Equal("Data Source=test.db", check.Config["connection_string"]);
        Assert.Equal("sqlite", check.Config["type"]);
    }

    [Fact]
    public void HealthCheckBuilder_ServiceCheck()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("Windows Update")
            .ServiceCheck("wuauserv")
            .Build();

        // Assert
        Assert.Equal(HealthCheckType.Service, check.Type);
        Assert.Equal("wuauserv", check.Config["service_name"]);
    }

    [Fact]
    public void HealthCheckBuilder_WithTimeout()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("Slow Check")
            .HttpCheck("https://slow.example.com")
            .WithTimeout(TimeSpan.FromMinutes(2))
            .Build();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(2), check.Timeout);
    }

    [Fact]
    public void HealthCheckBuilder_Disabled()
    {
        // Act
        var check = HealthCheckBuilder.Create()
            .WithName("Disabled Check")
            .HttpCheck("https://example.com")
            .Enabled(false)
            .Build();

        // Assert
        Assert.False(check.IsEnabled);
    }

    [Fact]
    public void HealthCheckBuilder_ThrowsWithoutName()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            HealthCheckBuilder.Create()
                .HttpCheck("https://example.com")
                .Build());
    }

    [Fact]
    public void CommonHealthChecks_LocalDatabase()
    {
        // Act
        var check = CommonHealthChecks.LocalDatabase("C:\\data\\test.db");

        // Assert
        Assert.Equal("Local Database", check.Name);
        Assert.Equal(HealthCheckType.Database, check.Type);
        Assert.Contains("C:\\data\\test.db", check.Config["connection_string"]);
    }

    [Fact]
    public void CommonHealthChecks_GoogleDns()
    {
        // Act
        var check = CommonHealthChecks.GoogleDns();

        // Assert
        Assert.Equal("Internet Connectivity (DNS)", check.Name);
        Assert.Equal(HealthCheckType.Ping, check.Type);
        Assert.Equal("8.8.8.8", check.Config["host"]);
    }

    [Fact]
    public void CommonHealthChecks_WebEndpoint()
    {
        // Act
        var check = CommonHealthChecks.WebEndpoint("My API", "https://api.myapp.com/health");

        // Assert
        Assert.Equal("My API", check.Name);
        Assert.Equal(HealthCheckType.Http, check.Type);
        Assert.Equal("https://api.myapp.com/health", check.Config["url"]);
    }

    [Fact]
    public void CommonHealthChecks_WindowsService()
    {
        // Act
        var check = CommonHealthChecks.WindowsService("Spooler", "Print Spooler");

        // Assert
        Assert.Equal("Print Spooler", check.Name);
        Assert.Equal(HealthCheckType.Service, check.Type);
        Assert.Equal("Spooler", check.Config["service_name"]);
    }

    [Fact]
    public void CommonHealthChecks_TcpPort()
    {
        // Act
        var check = CommonHealthChecks.TcpPort("Redis", "localhost", 6379);

        // Assert
        Assert.Equal("Redis", check.Name);
        Assert.Equal(HealthCheckType.Tcp, check.Type);
        Assert.Equal("localhost", check.Config["host"]);
        Assert.Equal("6379", check.Config["port"]);
    }

    [Fact]
    public void SystemHealthStatus_AllHealthy()
    {
        // Arrange
        var results = new List<HealthCheckResult>
        {
            new(HealthCheckId.New(), "Check1", HealthStatus.Healthy, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(100), null, null),
            new(HealthCheckId.New(), "Check2", HealthStatus.Healthy, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50), null, null),
            new(HealthCheckId.New(), "Check3", HealthStatus.Healthy, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(200), null, null),
        };

        // Act
        var status = new SystemHealthStatus(
            HealthStatus.Healthy,
            results.Count,
            3, // healthy
            0, // degraded
            0, // unhealthy
            0, // unknown
            DateTimeOffset.UtcNow,
            results
        );

        // Assert
        Assert.Equal(HealthStatus.Healthy, status.OverallStatus);
        Assert.Equal(100.0, status.HealthyPercentage);
        Assert.Equal(3, status.TotalChecks);
    }

    [Fact]
    public void SystemHealthStatus_MixedStatus()
    {
        // Arrange
        var results = new List<HealthCheckResult>
        {
            new(HealthCheckId.New(), "Check1", HealthStatus.Healthy, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(100), null, null),
            new(HealthCheckId.New(), "Check2", HealthStatus.Degraded, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50), null, null),
            new(HealthCheckId.New(), "Check3", HealthStatus.Unhealthy, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(200), null, null),
            new(HealthCheckId.New(), "Check4", HealthStatus.Unknown, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(0), null, null),
        };

        // Act
        var status = new SystemHealthStatus(
            HealthStatus.Unhealthy,
            results.Count,
            1, // healthy
            1, // degraded
            1, // unhealthy
            1, // unknown
            DateTimeOffset.UtcNow,
            results
        );

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, status.OverallStatus);
        Assert.Equal(25.0, status.HealthyPercentage);
    }

    [Fact]
    public void SystemHealthStatus_EmptyResults()
    {
        // Arrange & Act
        var status = new SystemHealthStatus(
            HealthStatus.Unknown,
            0,
            0, 0, 0, 0,
            DateTimeOffset.UtcNow,
            new List<HealthCheckResult>()
        );

        // Assert
        Assert.Equal(0.0, status.HealthyPercentage);
        Assert.Equal(0, status.TotalChecks);
    }

    [Fact]
    public void HealthCheckStatusChangedEventArgs_Properties()
    {
        // Arrange
        var checkId = HealthCheckId.New();
        var result = new HealthCheckResult(
            checkId,
            "Test",
            HealthStatus.Unhealthy,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            "Connection refused",
            null
        );

        // Act
        var args = new HealthCheckStatusChangedEventArgs
        {
            CheckId = checkId,
            CheckName = "Test",
            PreviousStatus = HealthStatus.Healthy,
            NewStatus = HealthStatus.Unhealthy,
            Result = result
        };

        // Assert
        Assert.Equal(HealthStatus.Healthy, args.PreviousStatus);
        Assert.Equal(HealthStatus.Unhealthy, args.NewStatus);
        Assert.Equal("Test", args.CheckName);
    }

    [Fact]
    public void SystemHealthChangedEventArgs_Properties()
    {
        // Arrange
        var currentHealth = new SystemHealthStatus(
            HealthStatus.Degraded,
            5, 3, 2, 0, 0,
            DateTimeOffset.UtcNow,
            new List<HealthCheckResult>()
        );

        // Act
        var args = new SystemHealthChangedEventArgs
        {
            PreviousStatus = HealthStatus.Healthy,
            NewStatus = HealthStatus.Degraded,
            CurrentHealth = currentHealth
        };

        // Assert
        Assert.Equal(HealthStatus.Healthy, args.PreviousStatus);
        Assert.Equal(HealthStatus.Degraded, args.NewStatus);
        Assert.NotNull(args.CurrentHealth);
    }

    [Fact]
    public void HealthCheckId_NewGeneratesUniqueIds()
    {
        // Act
        var id1 = HealthCheckId.New();
        var id2 = HealthCheckId.New();

        // Assert
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void HealthCheck_ConfigDictionary()
    {
        // Arrange
        var config = new Dictionary<string, string>
        {
            ["url"] = "https://example.com",
            ["method"] = "POST",
            ["expected_status"] = "201",
            ["headers"] = "Authorization:Bearer token123"
        };

        // Act
        var check = new HealthCheck(
            HealthCheckId.New(),
            "API Check",
            "",
            HealthCheckType.Http,
            config,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(30),
            true
        );

        // Assert
        Assert.Equal(4, check.Config.Count);
        Assert.Equal("https://example.com", check.Config["url"]);
        Assert.Equal("POST", check.Config["method"]);
    }

    [Fact]
    public void HealthCheckResult_Details()
    {
        // Arrange
        var details = new Dictionary<string, object>
        {
            ["status_code"] = 200,
            ["response_size"] = 1024,
            ["headers"] = new[] { "Content-Type: application/json" }
        };

        // Act
        var result = new HealthCheckResult(
            HealthCheckId.New(),
            "Test",
            HealthStatus.Healthy,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(100),
            "OK",
            details
        );

        // Assert
        Assert.NotNull(result.Details);
        Assert.Equal(200, result.Details["status_code"]);
        Assert.Equal(1024, result.Details["response_size"]);
    }

    [Fact]
    public void HealthCheckResult_NullDetails()
    {
        // Act
        var result = new HealthCheckResult(
            HealthCheckId.New(),
            "Test",
            HealthStatus.Healthy,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(100),
            "OK",
            null
        );

        // Assert
        Assert.Null(result.Details);
    }
}
