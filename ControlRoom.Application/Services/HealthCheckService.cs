using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.Services;

/// <summary>
/// Service that executes health checks and maintains overall system health status
/// </summary>
public interface IHealthCheckService : IDisposable
{
    /// <summary>
    /// Start the health check service
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the health check service
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Get all health check definitions
    /// </summary>
    IReadOnlyList<HealthCheck> GetHealthChecks();

    /// <summary>
    /// Get the latest results for all health checks
    /// </summary>
    IReadOnlyList<HealthCheckResult> GetLatestResults();

    /// <summary>
    /// Get overall system health status
    /// </summary>
    SystemHealthStatus GetSystemHealth();

    /// <summary>
    /// Execute a specific health check immediately
    /// </summary>
    Task<HealthCheckResult> ExecuteCheckAsync(HealthCheckId checkId);

    /// <summary>
    /// Create a new health check
    /// </summary>
    void CreateHealthCheck(HealthCheck check);

    /// <summary>
    /// Update a health check
    /// </summary>
    void UpdateHealthCheck(HealthCheck check);

    /// <summary>
    /// Delete a health check
    /// </summary>
    void DeleteHealthCheck(HealthCheckId checkId);

    /// <summary>
    /// Event raised when a health check status changes
    /// </summary>
    event EventHandler<HealthCheckStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// Event raised when overall system health changes
    /// </summary>
    event EventHandler<SystemHealthChangedEventArgs>? SystemHealthChanged;
}

/// <summary>
/// Overall system health status
/// </summary>
public sealed record SystemHealthStatus(
    HealthStatus OverallStatus,
    int TotalChecks,
    int HealthyCount,
    int DegradedCount,
    int UnhealthyCount,
    int UnknownCount,
    DateTimeOffset LastUpdated,
    IReadOnlyList<HealthCheckResult> Results
)
{
    public double HealthyPercentage => TotalChecks > 0
        ? (double)HealthyCount / TotalChecks * 100
        : 0;
}

/// <summary>
/// Event args when a health check status changes
/// </summary>
public sealed class HealthCheckStatusChangedEventArgs : EventArgs
{
    public required HealthCheckId CheckId { get; init; }
    public required string CheckName { get; init; }
    public required HealthStatus PreviousStatus { get; init; }
    public required HealthStatus NewStatus { get; init; }
    public required HealthCheckResult Result { get; init; }
}

/// <summary>
/// Event args when system health changes
/// </summary>
public sealed class SystemHealthChangedEventArgs : EventArgs
{
    public required HealthStatus PreviousStatus { get; init; }
    public required HealthStatus NewStatus { get; init; }
    public required SystemHealthStatus CurrentHealth { get; init; }
}

/// <summary>
/// Implementation of the health check service
/// </summary>
public sealed class HealthCheckService : IHealthCheckService
{
    private readonly MetricsQueries _metrics;
    private readonly ILogger<HealthCheckService>? _logger;
    private readonly HttpClient _httpClient;

    private readonly ConcurrentDictionary<HealthCheckId, HealthCheckResult> _latestResults = new();
    private readonly ConcurrentDictionary<HealthCheckId, DateTimeOffset> _lastExecutions = new();

    private CancellationTokenSource? _cts;
    private Task? _executionTask;
    private bool _disposed;
    private HealthStatus _lastOverallStatus = HealthStatus.Unknown;

    public event EventHandler<HealthCheckStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<SystemHealthChangedEventArgs>? SystemHealthChanged;

    public HealthCheckService(MetricsQueries metrics, ILogger<HealthCheckService>? logger = null)
    {
        _metrics = metrics;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null)
            throw new InvalidOperationException("Health check service is already running");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Load existing results
        var existingResults = _metrics.GetLatestHealthCheckResults();
        foreach (var result in existingResults)
        {
            _latestResults[result.CheckId] = result;
        }

        // Start the execution loop
        _executionTask = RunHealthCheckLoopAsync(_cts.Token);

        _logger?.LogInformation("Health check service started");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();

        if (_executionTask is not null)
        {
            try
            {
                await _executionTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _cts.Dispose();
        _cts = null;

        _logger?.LogInformation("Health check service stopped");
    }

    public IReadOnlyList<HealthCheck> GetHealthChecks()
    {
        return _metrics.GetEnabledHealthChecks();
    }

    public IReadOnlyList<HealthCheckResult> GetLatestResults()
    {
        return _latestResults.Values.OrderBy(r => r.CheckName).ToList();
    }

    public SystemHealthStatus GetSystemHealth()
    {
        var results = GetLatestResults();
        var totalChecks = results.Count;
        var healthyCount = results.Count(r => r.Status == HealthStatus.Healthy);
        var degradedCount = results.Count(r => r.Status == HealthStatus.Degraded);
        var unhealthyCount = results.Count(r => r.Status == HealthStatus.Unhealthy);
        var unknownCount = results.Count(r => r.Status == HealthStatus.Unknown);

        var overallStatus = DetermineOverallStatus(healthyCount, degradedCount, unhealthyCount, totalChecks);

        return new SystemHealthStatus(
            overallStatus,
            totalChecks,
            healthyCount,
            degradedCount,
            unhealthyCount,
            unknownCount,
            DateTimeOffset.UtcNow,
            results
        );
    }

    public async Task<HealthCheckResult> ExecuteCheckAsync(HealthCheckId checkId)
    {
        var checks = _metrics.GetEnabledHealthChecks();
        var check = checks.FirstOrDefault(c => c.Id == checkId);

        if (check is null)
            throw new InvalidOperationException($"Health check not found: {checkId}");

        return await ExecuteHealthCheckAsync(check);
    }

    public void CreateHealthCheck(HealthCheck check)
    {
        _metrics.CreateHealthCheck(check);
        _logger?.LogInformation("Created health check: {CheckName}", check.Name);
    }

    public void UpdateHealthCheck(HealthCheck check)
    {
        // TODO: Implement update in MetricsQueries
        _logger?.LogInformation("Updated health check: {CheckName}", check.Name);
    }

    public void DeleteHealthCheck(HealthCheckId checkId)
    {
        // TODO: Implement delete in MetricsQueries
        _latestResults.TryRemove(checkId, out _);
        _lastExecutions.TryRemove(checkId, out _);
        _logger?.LogInformation("Deleted health check: {CheckId}", checkId);
    }

    private async Task RunHealthCheckLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var checks = _metrics.GetEnabledHealthChecks();
                var now = DateTimeOffset.UtcNow;

                foreach (var check in checks)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    // Check if it's time to run this check
                    if (_lastExecutions.TryGetValue(check.Id, out var lastExec))
                    {
                        if (now - lastExec < check.Interval)
                            continue;
                    }

                    try
                    {
                        await ExecuteHealthCheckAsync(check);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error executing health check {CheckName}", check.Name);
                    }
                }

                // Check if overall status changed
                var currentHealth = GetSystemHealth();
                if (currentHealth.OverallStatus != _lastOverallStatus)
                {
                    var previousStatus = _lastOverallStatus;
                    _lastOverallStatus = currentHealth.OverallStatus;

                    SystemHealthChanged?.Invoke(this, new SystemHealthChangedEventArgs
                    {
                        PreviousStatus = previousStatus,
                        NewStatus = currentHealth.OverallStatus,
                        CurrentHealth = currentHealth
                    });
                }

                // Wait before next check cycle
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in health check loop");
            }
        }
    }

    private async Task<HealthCheckResult> ExecuteHealthCheckAsync(HealthCheck check)
    {
        var sw = Stopwatch.StartNew();
        HealthStatus status;
        string? message = null;
        Dictionary<string, object>? details = null;

        try
        {
            using var timeoutCts = new CancellationTokenSource(check.Timeout);

            var result = check.Type switch
            {
                HealthCheckType.Http => await ExecuteHttpCheckAsync(check, timeoutCts.Token),
                HealthCheckType.Tcp => await ExecuteTcpCheckAsync(check, timeoutCts.Token),
                HealthCheckType.Dns => await ExecuteDnsCheckAsync(check, timeoutCts.Token),
                HealthCheckType.Ping => await ExecutePingCheckAsync(check, timeoutCts.Token),
                HealthCheckType.Script => await ExecuteScriptCheckAsync(check, timeoutCts.Token),
                HealthCheckType.Database => await ExecuteDatabaseCheckAsync(check, timeoutCts.Token),
                HealthCheckType.Service => ExecuteServiceCheck(check),
                _ => (HealthStatus.Unknown, "Unknown health check type", null)
            };

            status = result.Status;
            message = result.Message;
            details = result.Details;
        }
        catch (OperationCanceledException)
        {
            status = HealthStatus.Unhealthy;
            message = $"Health check timed out after {check.Timeout.TotalSeconds}s";
        }
        catch (Exception ex)
        {
            status = HealthStatus.Unhealthy;
            message = $"Health check failed: {ex.Message}";
        }

        sw.Stop();

        var checkResult = new HealthCheckResult(
            check.Id,
            check.Name,
            status,
            DateTimeOffset.UtcNow,
            sw.Elapsed,
            message,
            details
        );

        // Record result
        _metrics.RecordHealthCheckResult(checkResult);
        _lastExecutions[check.Id] = DateTimeOffset.UtcNow;

        // Check for status change
        if (_latestResults.TryGetValue(check.Id, out var previousResult))
        {
            if (previousResult.Status != status)
            {
                StatusChanged?.Invoke(this, new HealthCheckStatusChangedEventArgs
                {
                    CheckId = check.Id,
                    CheckName = check.Name,
                    PreviousStatus = previousResult.Status,
                    NewStatus = status,
                    Result = checkResult
                });
            }
        }

        _latestResults[check.Id] = checkResult;

        _logger?.LogDebug("Health check {CheckName}: {Status} in {Duration}ms",
            check.Name, status, sw.ElapsedMilliseconds);

        return checkResult;
    }

    private async Task<(HealthStatus Status, string? Message, Dictionary<string, object>? Details)>
        ExecuteHttpCheckAsync(HealthCheck check, CancellationToken ct)
    {
        if (!check.Config.TryGetValue("url", out var url))
            return (HealthStatus.Unknown, "URL not configured", null);

        var method = check.Config.GetValueOrDefault("method", "GET");
        var expectedStatus = check.Config.TryGetValue("expected_status", out var es)
            ? int.Parse(es)
            : 200;

        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        // Add custom headers if configured
        if (check.Config.TryGetValue("headers", out var headers))
        {
            // Simple header parsing: "Header1:Value1;Header2:Value2"
            foreach (var header in headers.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = header.Split(':', 2);
                if (parts.Length == 2)
                {
                    request.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
                }
            }
        }

        var response = await _httpClient.SendAsync(request, ct);
        var statusCode = (int)response.StatusCode;

        var details = new Dictionary<string, object>
        {
            ["status_code"] = statusCode,
            ["url"] = url
        };

        if (statusCode == expectedStatus)
        {
            return (HealthStatus.Healthy, $"HTTP {statusCode} OK", details);
        }
        else if (statusCode >= 200 && statusCode < 400)
        {
            return (HealthStatus.Degraded, $"HTTP {statusCode} (expected {expectedStatus})", details);
        }
        else
        {
            return (HealthStatus.Unhealthy, $"HTTP {statusCode} {response.ReasonPhrase}", details);
        }
    }

    private async Task<(HealthStatus Status, string? Message, Dictionary<string, object>? Details)>
        ExecuteTcpCheckAsync(HealthCheck check, CancellationToken ct)
    {
        if (!check.Config.TryGetValue("host", out var host) ||
            !check.Config.TryGetValue("port", out var portStr) ||
            !int.TryParse(portStr, out var port))
        {
            return (HealthStatus.Unknown, "Host and port not configured", null);
        }

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);

        var details = new Dictionary<string, object>
        {
            ["host"] = host,
            ["port"] = port,
            ["connected"] = client.Connected
        };

        return client.Connected
            ? (HealthStatus.Healthy, $"TCP connection to {host}:{port} successful", details)
            : (HealthStatus.Unhealthy, $"TCP connection to {host}:{port} failed", details);
    }

    private async Task<(HealthStatus Status, string? Message, Dictionary<string, object>? Details)>
        ExecuteDnsCheckAsync(HealthCheck check, CancellationToken ct)
    {
        if (!check.Config.TryGetValue("hostname", out var hostname))
            return (HealthStatus.Unknown, "Hostname not configured", null);

        var addresses = await Dns.GetHostAddressesAsync(hostname, ct);

        var details = new Dictionary<string, object>
        {
            ["hostname"] = hostname,
            ["addresses"] = addresses.Select(a => a.ToString()).ToArray(),
            ["count"] = addresses.Length
        };

        return addresses.Length > 0
            ? (HealthStatus.Healthy, $"DNS resolved {hostname} to {addresses.Length} address(es)", details)
            : (HealthStatus.Unhealthy, $"DNS failed to resolve {hostname}", details);
    }

    private async Task<(HealthStatus Status, string? Message, Dictionary<string, object>? Details)>
        ExecutePingCheckAsync(HealthCheck check, CancellationToken ct)
    {
        if (!check.Config.TryGetValue("host", out var host))
            return (HealthStatus.Unknown, "Host not configured", null);

        using var ping = new Ping();
        var timeout = check.Config.TryGetValue("timeout_ms", out var tms)
            ? int.Parse(tms)
            : 3000;

        var reply = await ping.SendPingAsync(host, timeout);

        var details = new Dictionary<string, object>
        {
            ["host"] = host,
            ["status"] = reply.Status.ToString(),
            ["roundtrip_ms"] = reply.RoundtripTime,
            ["ttl"] = reply.Options?.Ttl ?? 0
        };

        return reply.Status == IPStatus.Success
            ? (HealthStatus.Healthy, $"Ping to {host} successful ({reply.RoundtripTime}ms)", details)
            : (HealthStatus.Unhealthy, $"Ping to {host} failed: {reply.Status}", details);
    }

    private async Task<(HealthStatus Status, string? Message, Dictionary<string, object>? Details)>
        ExecuteScriptCheckAsync(HealthCheck check, CancellationToken ct)
    {
        if (!check.Config.TryGetValue("script_path", out var scriptPath))
            return (HealthStatus.Unknown, "Script path not configured", null);

        var interpreter = check.Config.GetValueOrDefault("interpreter", "powershell");
        var args = check.Config.GetValueOrDefault("arguments", "");

        var startInfo = new ProcessStartInfo
        {
            FileName = interpreter,
            Arguments = interpreter.Contains("powershell")
                ? $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {args}"
                : $"\"{scriptPath}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var details = new Dictionary<string, object>
        {
            ["script_path"] = scriptPath,
            ["exit_code"] = process.ExitCode,
            ["output"] = output.Length > 500 ? output[..500] + "..." : output
        };

        // Exit code 0 = healthy, 1 = degraded, anything else = unhealthy
        var status = process.ExitCode switch
        {
            0 => HealthStatus.Healthy,
            1 => HealthStatus.Degraded,
            _ => HealthStatus.Unhealthy
        };

        return (status, $"Script exited with code {process.ExitCode}", details);
    }

    private async Task<(HealthStatus Status, string? Message, Dictionary<string, object>? Details)>
        ExecuteDatabaseCheckAsync(HealthCheck check, CancellationToken ct)
    {
        if (!check.Config.TryGetValue("connection_string", out var connectionString))
            return (HealthStatus.Unknown, "Connection string not configured", null);

        var dbType = check.Config.GetValueOrDefault("type", "sqlite");

        // For simplicity, just try to open and close the connection
        // In production, you'd use the appropriate ADO.NET provider
        try
        {
            if (dbType.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                await conn.OpenAsync(ct);

                var details = new Dictionary<string, object>
                {
                    ["database_type"] = dbType,
                    ["state"] = conn.State.ToString()
                };

                return (HealthStatus.Healthy, "Database connection successful", details);
            }
            else
            {
                return (HealthStatus.Unknown, $"Database type '{dbType}' not supported", null);
            }
        }
        catch (Exception ex)
        {
            return (HealthStatus.Unhealthy, $"Database connection failed: {ex.Message}", null);
        }
    }

    private (HealthStatus Status, string? Message, Dictionary<string, object>? Details)
        ExecuteServiceCheck(HealthCheck check)
    {
        if (!check.Config.TryGetValue("service_name", out var serviceName))
            return (HealthStatus.Unknown, "Service name not configured", null);

        // Only works on Windows
        if (!OperatingSystem.IsWindows())
            return (HealthStatus.Unknown, "Service checks only supported on Windows", null);

        try
        {
            // Use sc.exe to query service status (avoids ServiceController dependency)
            var startInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query \"{serviceName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // Parse the output to find STATE line
            var lines = output.Split('\n');
            var stateLine = lines.FirstOrDefault(l => l.Contains("STATE"));

            var details = new Dictionary<string, object>
            {
                ["service_name"] = serviceName,
                ["output"] = output.Length > 200 ? output[..200] : output
            };

            if (stateLine == null)
            {
                return (HealthStatus.Unhealthy, $"Service '{serviceName}' not found", details);
            }

            // STATE line format: "        STATE              : 4  RUNNING"
            if (stateLine.Contains("RUNNING"))
            {
                details["status"] = "Running";
                return (HealthStatus.Healthy, $"Service '{serviceName}' is Running", details);
            }
            else if (stateLine.Contains("START_PENDING") || stateLine.Contains("STOP_PENDING"))
            {
                details["status"] = "Pending";
                return (HealthStatus.Degraded, $"Service '{serviceName}' is in pending state", details);
            }
            else if (stateLine.Contains("STOPPED"))
            {
                details["status"] = "Stopped";
                return (HealthStatus.Unhealthy, $"Service '{serviceName}' is Stopped", details);
            }
            else
            {
                details["status"] = "Unknown";
                return (HealthStatus.Unknown, $"Service '{serviceName}' status unknown", details);
            }
        }
        catch (Exception ex)
        {
            return (HealthStatus.Unhealthy, $"Failed to check service '{serviceName}': {ex.Message}", null);
        }
    }

    private HealthStatus DetermineOverallStatus(int healthy, int degraded, int unhealthy, int total)
    {
        if (total == 0) return HealthStatus.Unknown;
        if (unhealthy > 0) return HealthStatus.Unhealthy;
        if (degraded > 0) return HealthStatus.Degraded;
        if (healthy == total) return HealthStatus.Healthy;
        return HealthStatus.Unknown;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAsync().GetAwaiter().GetResult();
        _httpClient.Dispose();
    }
}

/// <summary>
/// Builder for creating health checks with a fluent API
/// </summary>
public sealed class HealthCheckBuilder
{
    private string _name = "";
    private string _description = "";
    private HealthCheckType _type = HealthCheckType.Http;
    private Dictionary<string, string> _config = new();
    private TimeSpan _interval = TimeSpan.FromMinutes(1);
    private TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private bool _isEnabled = true;

    public static HealthCheckBuilder Create() => new();

    public HealthCheckBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public HealthCheckBuilder WithDescription(string description)
    {
        _description = description;
        return this;
    }

    public HealthCheckBuilder HttpCheck(string url, string method = "GET", int expectedStatus = 200)
    {
        _type = HealthCheckType.Http;
        _config["url"] = url;
        _config["method"] = method;
        _config["expected_status"] = expectedStatus.ToString();
        return this;
    }

    public HealthCheckBuilder TcpCheck(string host, int port)
    {
        _type = HealthCheckType.Tcp;
        _config["host"] = host;
        _config["port"] = port.ToString();
        return this;
    }

    public HealthCheckBuilder DnsCheck(string hostname)
    {
        _type = HealthCheckType.Dns;
        _config["hostname"] = hostname;
        return this;
    }

    public HealthCheckBuilder PingCheck(string host, int timeoutMs = 3000)
    {
        _type = HealthCheckType.Ping;
        _config["host"] = host;
        _config["timeout_ms"] = timeoutMs.ToString();
        return this;
    }

    public HealthCheckBuilder ScriptCheck(string scriptPath, string interpreter = "powershell", string? arguments = null)
    {
        _type = HealthCheckType.Script;
        _config["script_path"] = scriptPath;
        _config["interpreter"] = interpreter;
        if (arguments != null) _config["arguments"] = arguments;
        return this;
    }

    public HealthCheckBuilder DatabaseCheck(string connectionString, string dbType = "sqlite")
    {
        _type = HealthCheckType.Database;
        _config["connection_string"] = connectionString;
        _config["type"] = dbType;
        return this;
    }

    public HealthCheckBuilder ServiceCheck(string serviceName)
    {
        _type = HealthCheckType.Service;
        _config["service_name"] = serviceName;
        return this;
    }

    public HealthCheckBuilder WithInterval(TimeSpan interval)
    {
        _interval = interval;
        return this;
    }

    public HealthCheckBuilder WithTimeout(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    public HealthCheckBuilder Enabled(bool enabled = true)
    {
        _isEnabled = enabled;
        return this;
    }

    public HealthCheck Build()
    {
        if (string.IsNullOrWhiteSpace(_name))
            throw new InvalidOperationException("Name is required");

        return new HealthCheck(
            HealthCheckId.New(),
            _name,
            _description,
            _type,
            _config,
            _interval,
            _timeout,
            _isEnabled
        );
    }
}

/// <summary>
/// Common health check templates
/// </summary>
public static class CommonHealthChecks
{
    public static HealthCheck LocalDatabase(string dbPath)
    {
        return HealthCheckBuilder.Create()
            .WithName("Local Database")
            .WithDescription("Checks Control Room's SQLite database connectivity")
            .DatabaseCheck($"Data Source={dbPath}")
            .WithInterval(TimeSpan.FromMinutes(1))
            .Build();
    }

    public static HealthCheck GoogleDns()
    {
        return HealthCheckBuilder.Create()
            .WithName("Internet Connectivity (DNS)")
            .WithDescription("Checks internet connectivity via Google DNS")
            .PingCheck("8.8.8.8")
            .WithInterval(TimeSpan.FromMinutes(5))
            .Build();
    }

    public static HealthCheck WebEndpoint(string name, string url)
    {
        return HealthCheckBuilder.Create()
            .WithName(name)
            .WithDescription($"HTTP health check for {url}")
            .HttpCheck(url)
            .WithInterval(TimeSpan.FromMinutes(1))
            .Build();
    }

    public static HealthCheck WindowsService(string serviceName, string displayName)
    {
        return HealthCheckBuilder.Create()
            .WithName(displayName)
            .WithDescription($"Checks if Windows service '{serviceName}' is running")
            .ServiceCheck(serviceName)
            .WithInterval(TimeSpan.FromMinutes(1))
            .Build();
    }

    public static HealthCheck TcpPort(string name, string host, int port)
    {
        return HealthCheckBuilder.Create()
            .WithName(name)
            .WithDescription($"TCP port check for {host}:{port}")
            .TcpCheck(host, port)
            .WithInterval(TimeSpan.FromMinutes(1))
            .Build();
    }
}
