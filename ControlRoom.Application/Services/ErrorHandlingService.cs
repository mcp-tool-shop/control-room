using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ControlRoom.Application.Services;

/// <summary>
/// Friendly error handling service that makes errors helpful, human, and recoverable.
/// Implements the "3 Questions" rule: What happened? What can I do? What if I do nothing?
/// </summary>
public sealed class ErrorHandlingService
{
    private readonly IErrorLogRepository _logRepository;
    private readonly List<ErrorRecord> _recentErrors = [];
    private readonly object _lock = new();

    public ErrorHandlingService(IErrorLogRepository logRepository)
    {
        _logRepository = logRepository;
    }

    /// <summary>
    /// Creates a friendly error from an exception.
    /// </summary>
    public FriendlyError CreateFriendlyError(
        Exception exception,
        ErrorContext context,
        string? userMessage = null)
    {
        var errorType = ClassifyException(exception);
        var correlationId = GenerateCorrelationId();

        var friendlyError = new FriendlyError(
            CorrelationId: correlationId,
            Type: errorType,
            Title: GetFriendlyTitle(errorType, context, exception),
            Message: userMessage ?? GetFriendlyMessage(errorType, context, exception),
            Impact: GetImpactMessage(errorType, context),
            PrimaryAction: GetPrimaryAction(errorType, context),
            SecondaryActions: GetSecondaryActions(errorType, context),
            TechnicalDetails: CreateTechnicalDetails(exception, context, correlationId),
            Timestamp: DateTimeOffset.UtcNow,
            Context: context,
            IsRecoverable: IsRecoverable(errorType),
            SuggestedRetryDelay: GetSuggestedRetryDelay(errorType, exception));

        LogError(friendlyError, exception);
        return friendlyError;
    }

    /// <summary>
    /// Creates a friendly error for validation failures.
    /// </summary>
    public FriendlyError CreateValidationError(
        string field,
        string message,
        object? currentValue = null)
    {
        return new FriendlyError(
            CorrelationId: GenerateCorrelationId(),
            Type: ErrorType.Validation,
            Title: "Please check your input",
            Message: message,
            Impact: null,
            PrimaryAction: new ErrorAction("Fix", "action.focus-field", field, "\uE70F"),
            SecondaryActions: [],
            TechnicalDetails: new TechnicalDetails(
                ErrorCode: "VALIDATION_ERROR",
                Component: field,
                Timestamp: DateTimeOffset.UtcNow,
                ExceptionType: null,
                StackTrace: null,
                InnerError: null,
                AdditionalData: currentValue != null
                    ? new Dictionary<string, object> { ["currentValue"] = currentValue }
                    : null),
            Timestamp: DateTimeOffset.UtcNow,
            Context: new ErrorContext(ErrorSource.UserInput, field),
            IsRecoverable: true,
            SuggestedRetryDelay: null);
    }

    /// <summary>
    /// Creates a friendly error for connection issues.
    /// </summary>
    public FriendlyError CreateConnectionError(
        string serviceName,
        bool isOffline,
        Exception? innerException = null)
    {
        var context = new ErrorContext(ErrorSource.ExternalService, serviceName);

        return new FriendlyError(
            CorrelationId: GenerateCorrelationId(),
            Type: isOffline ? ErrorType.Offline : ErrorType.Connection,
            Title: isOffline
                ? "You're offline"
                : $"Couldn't connect to {serviceName}",
            Message: isOffline
                ? "Check your internet connection. Your work is saved locally."
                : $"We couldn't reach {serviceName}. It might be temporarily unavailable.",
            Impact: isOffline
                ? "Runs will still work locally. Sync will resume when you're back online."
                : $"Changes won't sync to {serviceName} until the connection is restored.",
            PrimaryAction: new ErrorAction("Retry", "action.retry", null, "\uE72C"),
            SecondaryActions:
            [
                new ErrorAction("Check connection", "action.check-connection", null, "\uE774"),
                new ErrorAction("Work offline", "action.work-offline", null, "\uE8F4")
            ],
            TechnicalDetails: innerException != null
                ? CreateTechnicalDetails(innerException, context, GenerateCorrelationId())
                : null,
            Timestamp: DateTimeOffset.UtcNow,
            Context: context,
            IsRecoverable: true,
            SuggestedRetryDelay: TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Creates a friendly error for authentication issues.
    /// </summary>
    public FriendlyError CreateAuthError(
        string serviceName,
        AuthErrorReason reason)
    {
        var (title, message, primaryAction) = reason switch
        {
            AuthErrorReason.Expired => (
                $"Your {serviceName} connection needs attention",
                "Your login has expired. Reconnect to keep syncing.",
                new ErrorAction("Reconnect", "action.reconnect", serviceName, "\uE8D7")),

            AuthErrorReason.Revoked => (
                $"{serviceName} access was revoked",
                "The connection was disconnected from the other side. You'll need to reconnect.",
                new ErrorAction("Reconnect", "action.reconnect", serviceName, "\uE8D7")),

            AuthErrorReason.InsufficientScope => (
                $"{serviceName} needs more permissions",
                "We need additional access to complete this action.",
                new ErrorAction("Update permissions", "action.update-permissions", serviceName, "\uE8D7")),

            AuthErrorReason.InvalidCredentials => (
                $"Couldn't sign in to {serviceName}",
                "Please check your credentials and try again.",
                new ErrorAction("Try again", "action.retry-auth", serviceName, "\uE8D7")),

            _ => (
                $"{serviceName} authentication failed",
                "There was a problem with authentication. Please try reconnecting.",
                new ErrorAction("Reconnect", "action.reconnect", serviceName, "\uE8D7"))
        };

        return new FriendlyError(
            CorrelationId: GenerateCorrelationId(),
            Type: ErrorType.Authentication,
            Title: title,
            Message: message,
            Impact: $"Changes won't sync to {serviceName} until you reconnect.",
            PrimaryAction: primaryAction,
            SecondaryActions:
            [
                new ErrorAction("Not now", "action.dismiss", null, "\uE711"),
                new ErrorAction("View details", "action.view-details", null, "\uE946")
            ],
            TechnicalDetails: new TechnicalDetails(
                ErrorCode: $"AUTH_{reason.ToString().ToUpperInvariant()}",
                Component: serviceName,
                Timestamp: DateTimeOffset.UtcNow,
                ExceptionType: null,
                StackTrace: null,
                InnerError: null,
                AdditionalData: new Dictionary<string, object> { ["reason"] = reason.ToString() }),
            Timestamp: DateTimeOffset.UtcNow,
            Context: new ErrorContext(ErrorSource.ExternalService, serviceName),
            IsRecoverable: true,
            SuggestedRetryDelay: null);
    }

    /// <summary>
    /// Creates a friendly error for permission issues.
    /// </summary>
    public FriendlyError CreatePermissionError(
        string resource,
        string? requiredPermission = null,
        string? adminContact = null)
    {
        var message = requiredPermission != null
            ? $"Ask a team admin to grant {requiredPermission} for {resource}."
            : $"You don't have access to {resource}.";

        return new FriendlyError(
            CorrelationId: GenerateCorrelationId(),
            Type: ErrorType.Permission,
            Title: "You don't have access to do that",
            Message: message,
            Impact: null,
            PrimaryAction: adminContact != null
                ? new ErrorAction("Request access", "action.request-access", resource, "\uE8FA")
                : new ErrorAction("Go back", "action.go-back", null, "\uE72B"),
            SecondaryActions:
            [
                new ErrorAction("View details", "action.view-details", null, "\uE946")
            ],
            TechnicalDetails: new TechnicalDetails(
                ErrorCode: "PERMISSION_DENIED",
                Component: resource,
                Timestamp: DateTimeOffset.UtcNow,
                ExceptionType: null,
                StackTrace: null,
                InnerError: null,
                AdditionalData: requiredPermission != null
                    ? new Dictionary<string, object> { ["requiredPermission"] = requiredPermission }
                    : null),
            Timestamp: DateTimeOffset.UtcNow,
            Context: new ErrorContext(ErrorSource.Authorization, resource),
            IsRecoverable: false,
            SuggestedRetryDelay: null);
    }

    /// <summary>
    /// Creates a friendly error for rate limiting.
    /// </summary>
    public FriendlyError CreateRateLimitError(
        string serviceName,
        TimeSpan? retryAfter = null)
    {
        var retryTime = retryAfter ?? TimeSpan.FromMinutes(1);
        var retryMessage = retryTime.TotalMinutes >= 1
            ? $"{(int)retryTime.TotalMinutes} minute{((int)retryTime.TotalMinutes != 1 ? "s" : "")}"
            : $"{(int)retryTime.TotalSeconds} seconds";

        return new FriendlyError(
            CorrelationId: GenerateCorrelationId(),
            Type: ErrorType.RateLimit,
            Title: $"{serviceName} is throttling requests",
            Message: $"We've made too many requests. We'll retry automatically in {retryMessage}.",
            Impact: "Operations are queued and will complete when limits reset.",
            PrimaryAction: new ErrorAction("Retry now", "action.retry", null, "\uE72C"),
            SecondaryActions:
            [
                new ErrorAction("View details", "action.view-details", null, "\uE946")
            ],
            TechnicalDetails: new TechnicalDetails(
                ErrorCode: "RATE_LIMITED",
                Component: serviceName,
                Timestamp: DateTimeOffset.UtcNow,
                ExceptionType: null,
                StackTrace: null,
                InnerError: null,
                AdditionalData: new Dictionary<string, object>
                {
                    ["retryAfterSeconds"] = retryTime.TotalSeconds
                }),
            Timestamp: DateTimeOffset.UtcNow,
            Context: new ErrorContext(ErrorSource.ExternalService, serviceName),
            IsRecoverable: true,
            SuggestedRetryDelay: retryTime);
    }

    /// <summary>
    /// Creates a friendly error for run/runbook failures.
    /// </summary>
    public FriendlyError CreateRunError(
        string runId,
        string? runbookName,
        int? failedStep,
        int? totalSteps,
        string? stepName,
        string? errorOutput,
        Exception? exception = null)
    {
        var stepInfo = failedStep.HasValue && totalSteps.HasValue
            ? $" at step {failedStep} of {totalSteps}"
            : "";
        var stepNameInfo = !string.IsNullOrEmpty(stepName) ? $" ({stepName})" : "";

        return new FriendlyError(
            CorrelationId: GenerateCorrelationId(),
            Type: ErrorType.RunFailed,
            Title: runbookName != null
                ? $"Runbook '{runbookName}' failed{stepInfo}"
                : $"Run failed{stepInfo}",
            Message: !string.IsNullOrEmpty(stepNameInfo)
                ? $"Failed{stepNameInfo}. Check the output for details."
                : "The run didn't complete successfully. Check the output for details.",
            Impact: failedStep.HasValue && totalSteps.HasValue && failedStep < totalSteps
                ? $"Steps 1-{failedStep - 1} completed. Steps {failedStep}-{totalSteps} were not run."
                : null,
            PrimaryAction: failedStep.HasValue
                ? new ErrorAction("Retry from failed step", "action.retry-step", runId, "\uE72C")
                : new ErrorAction("Retry", "action.retry-run", runId, "\uE72C"),
            SecondaryActions:
            [
                new ErrorAction("View output", "action.view-output", runId, "\uE8A5"),
                new ErrorAction("Restart entire run", "action.restart-run", runId, "\uE72C"),
                new ErrorAction("Copy error", "action.copy-error", null, "\uE8C8")
            ],
            TechnicalDetails: new TechnicalDetails(
                ErrorCode: "RUN_FAILED",
                Component: runbookName ?? runId,
                Timestamp: DateTimeOffset.UtcNow,
                ExceptionType: exception?.GetType().Name,
                StackTrace: exception?.StackTrace,
                InnerError: exception?.InnerException?.Message,
                AdditionalData: new Dictionary<string, object>
                {
                    ["runId"] = runId,
                    ["failedStep"] = failedStep ?? -1,
                    ["totalSteps"] = totalSteps ?? -1,
                    ["stepName"] = stepName ?? "",
                    ["errorOutput"] = TruncateForLog(errorOutput, 2000)
                }),
            Timestamp: DateTimeOffset.UtcNow,
            Context: new ErrorContext(ErrorSource.Run, runId),
            IsRecoverable: true,
            SuggestedRetryDelay: null);
    }

    /// <summary>
    /// Creates a friendly error for sync conflicts.
    /// </summary>
    public FriendlyError CreateSyncConflictError(
        string resourceType,
        string resourceId,
        DateTimeOffset localModified,
        DateTimeOffset remoteModified)
    {
        return new FriendlyError(
            CorrelationId: GenerateCorrelationId(),
            Type: ErrorType.SyncConflict,
            Title: $"Conflict in {resourceType}",
            Message: "This item was edited both locally and remotely. Choose which version to keep.",
            Impact: "The item won't sync until you resolve the conflict.",
            PrimaryAction: new ErrorAction("Keep local", "action.keep-local", resourceId, "\uE8F4"),
            SecondaryActions:
            [
                new ErrorAction("Take remote", "action.take-remote", resourceId, "\uE753"),
                new ErrorAction("Compare", "action.compare-versions", resourceId, "\uE8E5"),
                new ErrorAction("Keep both", "action.keep-both", resourceId, "\uE8C8")
            ],
            TechnicalDetails: new TechnicalDetails(
                ErrorCode: "SYNC_CONFLICT",
                Component: resourceType,
                Timestamp: DateTimeOffset.UtcNow,
                ExceptionType: null,
                StackTrace: null,
                InnerError: null,
                AdditionalData: new Dictionary<string, object>
                {
                    ["resourceId"] = resourceId,
                    ["localModified"] = localModified,
                    ["remoteModified"] = remoteModified
                }),
            Timestamp: DateTimeOffset.UtcNow,
            Context: new ErrorContext(ErrorSource.Sync, resourceId),
            IsRecoverable: true,
            SuggestedRetryDelay: null);
    }

    /// <summary>
    /// Creates a support bundle for sharing with support team.
    /// </summary>
    public async Task<SupportBundle> CreateSupportBundleAsync(
        FriendlyError error,
        CancellationToken cancellationToken = default)
    {
        var recentLogs = await _logRepository.GetRecentLogsAsync(50, cancellationToken);

        return new SupportBundle(
            CorrelationId: error.CorrelationId,
            AppVersion: GetAppVersion(),
            Platform: GetPlatformInfo(),
            Timestamp: error.Timestamp,
            ErrorSummary: $"{error.Title}: {error.Message}",
            TechnicalDetails: error.TechnicalDetails,
            RecentLogs: recentLogs.Select(l => new LogEntry(
                l.Timestamp,
                l.Level,
                l.Message,
                RedactSecrets(l.Details))).ToList(),
            SystemInfo: GetSystemInfo(),
            IntegrationStates: await GetIntegrationStatesAsync(cancellationToken));
    }

    /// <summary>
    /// Formats a support bundle as copyable text.
    /// </summary>
    public static string FormatSupportBundle(SupportBundle bundle)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Control Room Error Report ===");
        sb.AppendLine($"Correlation ID: {bundle.CorrelationId}");
        sb.AppendLine($"Timestamp: {bundle.Timestamp:O}");
        sb.AppendLine($"App Version: {bundle.AppVersion}");
        sb.AppendLine($"Platform: {bundle.Platform}");
        sb.AppendLine();
        sb.AppendLine("--- Error ---");
        sb.AppendLine(bundle.ErrorSummary);
        sb.AppendLine();

        if (bundle.TechnicalDetails != null)
        {
            sb.AppendLine("--- Technical Details ---");
            sb.AppendLine($"Error Code: {bundle.TechnicalDetails.ErrorCode}");
            sb.AppendLine($"Component: {bundle.TechnicalDetails.Component}");
            if (bundle.TechnicalDetails.ExceptionType != null)
            {
                sb.AppendLine($"Exception: {bundle.TechnicalDetails.ExceptionType}");
            }
            if (bundle.TechnicalDetails.InnerError != null)
            {
                sb.AppendLine($"Inner Error: {bundle.TechnicalDetails.InnerError}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("--- System Info ---");
        foreach (var (key, value) in bundle.SystemInfo)
        {
            sb.AppendLine($"{key}: {value}");
        }
        sb.AppendLine();

        sb.AppendLine("--- Recent Logs (last 10) ---");
        foreach (var log in bundle.RecentLogs.TakeLast(10))
        {
            sb.AppendLine($"[{log.Timestamp:HH:mm:ss}] [{log.Level}] {log.Message}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the suggested UI placement for an error.
    /// </summary>
    public static ErrorPlacement GetSuggestedPlacement(FriendlyError error)
    {
        return error.Type switch
        {
            ErrorType.Validation => ErrorPlacement.Inline,
            ErrorType.Offline => ErrorPlacement.Banner,
            ErrorType.Connection when !error.Context.IsBlocking => ErrorPlacement.Toast,
            ErrorType.Connection => ErrorPlacement.Banner,
            ErrorType.RateLimit => ErrorPlacement.Toast,
            ErrorType.Authentication => ErrorPlacement.Modal,
            ErrorType.Permission => ErrorPlacement.Modal,
            ErrorType.SyncConflict => ErrorPlacement.Modal,
            ErrorType.RunFailed => ErrorPlacement.Toast,
            ErrorType.DataCorruption => ErrorPlacement.Modal,
            ErrorType.Configuration => ErrorPlacement.Modal,
            _ => ErrorPlacement.Toast
        };
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private static ErrorType ClassifyException(Exception exception)
    {
        return exception switch
        {
            HttpRequestException => ErrorType.Connection,
            TaskCanceledException { InnerException: TimeoutException } => ErrorType.Timeout,
            TimeoutException => ErrorType.Timeout,
            UnauthorizedAccessException => ErrorType.Permission,
            InvalidOperationException { Message: var m } when m.Contains("not configured") => ErrorType.Configuration,
            ArgumentException or ArgumentNullException => ErrorType.Validation,
            JsonException => ErrorType.DataCorruption,
            IOException => ErrorType.Storage,
            _ => ErrorType.Unknown
        };
    }

    private static string GetFriendlyTitle(ErrorType type, ErrorContext context, Exception exception)
    {
        return type switch
        {
            ErrorType.Connection => $"Couldn't connect to {context.Component ?? "the service"}",
            ErrorType.Timeout => $"{context.Component ?? "The operation"} took too long",
            ErrorType.Permission => "You don't have access to do that",
            ErrorType.Authentication => $"{context.Component ?? "Service"} authentication failed",
            ErrorType.Validation => "Please check your input",
            ErrorType.Configuration => $"{context.Component ?? "The service"} isn't configured correctly",
            ErrorType.RateLimit => $"{context.Component ?? "The service"} is throttling requests",
            ErrorType.Storage => "Couldn't save your changes",
            ErrorType.DataCorruption => "Something doesn't look right",
            ErrorType.Offline => "You're offline",
            ErrorType.SyncConflict => "There's a conflict to resolve",
            ErrorType.RunFailed => "The run didn't complete",
            _ => "Something went wrong"
        };
    }

    private static string GetFriendlyMessage(ErrorType type, ErrorContext context, Exception exception)
    {
        return type switch
        {
            ErrorType.Connection => $"We couldn't reach {context.Component ?? "the service"}. Check your connection or try again.",
            ErrorType.Timeout => "The operation took longer than expected. You can try again or check if the service is responding.",
            ErrorType.Permission => $"You don't have the required permissions for {context.Component ?? "this action"}.",
            ErrorType.Authentication => "Your session may have expired. Please sign in again.",
            ErrorType.Validation => exception.Message,
            ErrorType.Configuration => $"There's a configuration issue with {context.Component ?? "the service"}. Check the settings.",
            ErrorType.RateLimit => "We've made too many requests. We'll retry automatically.",
            ErrorType.Storage => "We couldn't save to disk. Check available space and permissions.",
            ErrorType.DataCorruption => "Some data appears to be corrupted. We'll try to recover what we can.",
            ErrorType.Offline => "You're not connected to the internet. Your changes are saved locally.",
            ErrorType.SyncConflict => "This item was changed in multiple places. Choose which version to keep.",
            ErrorType.RunFailed => "The script or runbook didn't complete successfully. Check the output for details.",
            _ => "We ran into an unexpected problem. Try again or contact support if it continues."
        };
    }

    private static string? GetImpactMessage(ErrorType type, ErrorContext context)
    {
        return type switch
        {
            ErrorType.Connection => $"Changes won't sync until the connection is restored.",
            ErrorType.Offline => "Runs still work locally. Sync will resume when you're back online.",
            ErrorType.Authentication => $"Features requiring {context.Component ?? "this service"} won't work until you reconnect.",
            ErrorType.SyncConflict => "This item won't sync until you resolve the conflict.",
            _ => null
        };
    }

    private static ErrorAction GetPrimaryAction(ErrorType type, ErrorContext context)
    {
        return type switch
        {
            ErrorType.Connection => new ErrorAction("Retry", "action.retry", null, "\uE72C"),
            ErrorType.Timeout => new ErrorAction("Try again", "action.retry", null, "\uE72C"),
            ErrorType.Authentication => new ErrorAction("Reconnect", "action.reconnect", context.Component, "\uE8D7"),
            ErrorType.Permission => new ErrorAction("Go back", "action.go-back", null, "\uE72B"),
            ErrorType.Validation => new ErrorAction("Fix", "action.focus-field", context.Component, "\uE70F"),
            ErrorType.Configuration => new ErrorAction("Open settings", "action.open-settings", context.Component, "\uE713"),
            ErrorType.RateLimit => new ErrorAction("Retry now", "action.retry", null, "\uE72C"),
            ErrorType.Storage => new ErrorAction("Try again", "action.retry", null, "\uE72C"),
            ErrorType.Offline => new ErrorAction("Work offline", "action.work-offline", null, "\uE8F4"),
            ErrorType.SyncConflict => new ErrorAction("Resolve", "action.resolve-conflict", context.Component, "\uE8E5"),
            ErrorType.RunFailed => new ErrorAction("View output", "action.view-output", context.Component, "\uE8A5"),
            _ => new ErrorAction("Retry", "action.retry", null, "\uE72C")
        };
    }

    private static IReadOnlyList<ErrorAction> GetSecondaryActions(ErrorType type, ErrorContext context)
    {
        var actions = new List<ErrorAction>
        {
            new("View details", "action.view-details", null, "\uE946")
        };

        if (type is ErrorType.Connection or ErrorType.Offline)
        {
            actions.Insert(0, new ErrorAction("Check connection", "action.check-connection", null, "\uE774"));
        }

        if (type == ErrorType.Authentication)
        {
            actions.Insert(0, new ErrorAction("Not now", "action.dismiss", null, "\uE711"));
        }

        if (type is ErrorType.Storage or ErrorType.DataCorruption)
        {
            actions.Insert(0, new ErrorAction("Open logs", "action.open-logs", null, "\uE8A5"));
        }

        return actions;
    }

    private static TechnicalDetails CreateTechnicalDetails(
        Exception exception,
        ErrorContext context,
        string correlationId)
    {
        return new TechnicalDetails(
            ErrorCode: $"{ClassifyException(exception).ToString().ToUpperInvariant()}_{correlationId[..8]}",
            Component: context.Component ?? "Unknown",
            Timestamp: DateTimeOffset.UtcNow,
            ExceptionType: exception.GetType().FullName,
            StackTrace: exception.StackTrace,
            InnerError: exception.InnerException?.Message,
            AdditionalData: new Dictionary<string, object>
            {
                ["source"] = context.Source.ToString(),
                ["message"] = exception.Message
            });
    }

    private static bool IsRecoverable(ErrorType type)
    {
        return type switch
        {
            ErrorType.DataCorruption => false,
            ErrorType.Permission => false,
            _ => true
        };
    }

    private static TimeSpan? GetSuggestedRetryDelay(ErrorType type, Exception exception)
    {
        return type switch
        {
            ErrorType.Connection => TimeSpan.FromSeconds(5),
            ErrorType.Timeout => TimeSpan.FromSeconds(10),
            ErrorType.RateLimit => TimeSpan.FromMinutes(1),
            ErrorType.Storage => TimeSpan.FromSeconds(2),
            _ => null
        };
    }

    private static string GenerateCorrelationId()
    {
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void LogError(FriendlyError error, Exception? exception)
    {
        lock (_lock)
        {
            _recentErrors.Add(new ErrorRecord(error, exception, DateTimeOffset.UtcNow));
            if (_recentErrors.Count > 100)
            {
                _recentErrors.RemoveAt(0);
            }
        }

        // Async log to repository (fire and forget)
        _ = _logRepository.LogErrorAsync(error, exception);
    }

    private static string TruncateForLog(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static string RedactSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Redact common secret patterns
        var patterns = new[]
        {
            (@"(api[_-]?key[""']?\s*[:=]\s*[""']?)[a-zA-Z0-9\-_]+", "$1[REDACTED]"),
            (@"(token[""']?\s*[:=]\s*[""']?)[a-zA-Z0-9\-_.]+", "$1[REDACTED]"),
            (@"(password[""']?\s*[:=]\s*[""']?)[^\s""']+", "$1[REDACTED]"),
            (@"(secret[""']?\s*[:=]\s*[""']?)[a-zA-Z0-9\-_]+", "$1[REDACTED]"),
            (@"(bearer\s+)[a-zA-Z0-9\-_.]+", "$1[REDACTED]"),
            (@"(authorization[""']?\s*[:=]\s*[""']?)[^\s""']+", "$1[REDACTED]")
        };

        foreach (var (pattern, replacement) in patterns)
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text, pattern, replacement,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return text;
    }

    private static string GetAppVersion()
    {
        return typeof(ErrorHandlingService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    private static string GetPlatformInfo()
    {
        return $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}";
    }

    private static Dictionary<string, string> GetSystemInfo()
    {
        return new Dictionary<string, string>
        {
            ["OS"] = Environment.OSVersion.ToString(),
            ["Architecture"] = Environment.Is64BitOperatingSystem ? "x64" : "x86",
            [".NET Version"] = Environment.Version.ToString(),
            ["Machine"] = Environment.MachineName,
            ["Processors"] = Environment.ProcessorCount.ToString(),
            ["Memory"] = $"{GC.GetTotalMemory(false) / 1024 / 1024} MB used"
        };
    }

    private async Task<Dictionary<string, string>> GetIntegrationStatesAsync(
        CancellationToken cancellationToken)
    {
        // Would query actual integration states
        return new Dictionary<string, string>
        {
            ["GitHub"] = "Connected",
            ["Slack"] = "Connected",
            ["AWS"] = "Not configured"
        };
    }
}

// ============================================================================
// Error Types
// ============================================================================

/// <summary>
/// A user-friendly error with actionable information.
/// </summary>
public sealed record FriendlyError(
    string CorrelationId,
    ErrorType Type,
    string Title,
    string Message,
    string? Impact,
    ErrorAction PrimaryAction,
    IReadOnlyList<ErrorAction> SecondaryActions,
    TechnicalDetails? TechnicalDetails,
    DateTimeOffset Timestamp,
    ErrorContext Context,
    bool IsRecoverable,
    TimeSpan? SuggestedRetryDelay);

/// <summary>
/// Action that can be taken in response to an error.
/// </summary>
public sealed record ErrorAction(
    string Label,
    string CommandId,
    string? Parameter,
    string Icon);

/// <summary>
/// Technical details for debugging.
/// </summary>
public sealed record TechnicalDetails(
    string ErrorCode,
    string Component,
    DateTimeOffset Timestamp,
    string? ExceptionType,
    string? StackTrace,
    string? InnerError,
    Dictionary<string, object>? AdditionalData);

/// <summary>
/// Context about where the error occurred.
/// </summary>
public sealed record ErrorContext(
    ErrorSource Source,
    string? Component = null,
    bool IsBlocking = false,
    string? UserId = null,
    string? SessionId = null);

/// <summary>
/// Support bundle for sharing with support team.
/// </summary>
public sealed record SupportBundle(
    string CorrelationId,
    string AppVersion,
    string Platform,
    DateTimeOffset Timestamp,
    string ErrorSummary,
    TechnicalDetails? TechnicalDetails,
    IReadOnlyList<LogEntry> RecentLogs,
    Dictionary<string, string> SystemInfo,
    Dictionary<string, string> IntegrationStates);

/// <summary>
/// Log entry for support bundle.
/// </summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Details);

/// <summary>
/// Internal error record.
/// </summary>
internal sealed record ErrorRecord(
    FriendlyError Error,
    Exception? Exception,
    DateTimeOffset Timestamp);

/// <summary>
/// Type of error.
/// </summary>
public enum ErrorType
{
    Unknown,
    Validation,
    Connection,
    Timeout,
    Authentication,
    Permission,
    Configuration,
    RateLimit,
    Storage,
    DataCorruption,
    Offline,
    SyncConflict,
    RunFailed,
    WebhookFailed
}

/// <summary>
/// Source of the error.
/// </summary>
public enum ErrorSource
{
    Unknown,
    UserInput,
    ExternalService,
    InternalService,
    Authorization,
    Sync,
    Run,
    Webhook,
    Database,
    FileSystem
}

/// <summary>
/// Where to display the error.
/// </summary>
public enum ErrorPlacement
{
    Inline,     // Next to the field/element
    Toast,      // Quick non-blocking notification
    Banner,     // Persistent banner at top
    Modal       // Blocking dialog
}

/// <summary>
/// Reason for authentication error.
/// </summary>
public enum AuthErrorReason
{
    Expired,
    Revoked,
    InsufficientScope,
    InvalidCredentials,
    Unknown
}

/// <summary>
/// Repository for error logging.
/// </summary>
public interface IErrorLogRepository
{
    Task LogErrorAsync(FriendlyError error, Exception? exception);
    Task<IReadOnlyList<LogEntryRecord>> GetRecentLogsAsync(int count, CancellationToken cancellationToken);
}

/// <summary>
/// Log entry from repository.
/// </summary>
public sealed record LogEntryRecord(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Details);
