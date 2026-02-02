namespace ControlRoom.Application.Services;

/// <summary>
/// Kill Switch & Safety Valve: Provides emergency controls to stop operations,
/// pause integrations, and recover from failures safely.
///
/// Checklist items addressed:
/// - Emergency stop exists
/// - Per-integration pause
/// - Safe mode startup
/// - Credential revocation path
/// </summary>
public sealed class KillSwitchService
{
    private readonly IKillSwitchRepository _repository;
    private readonly IAuditLogRepository _auditRepository;
    private volatile bool _globalEmergencyStop;
    private readonly Dictionary<string, bool> _integrationPaused = new();
    private readonly object _lock = new();

    public event EventHandler<EmergencyStopEventArgs>? EmergencyStopTriggered;
    public event EventHandler<IntegrationPausedEventArgs>? IntegrationPaused;
    public event EventHandler<SafeModeEventArgs>? SafeModeActivated;

    public KillSwitchService(
        IKillSwitchRepository repository,
        IAuditLogRepository auditRepository)
    {
        _repository = repository;
        _auditRepository = auditRepository;
    }

    // ========================================================================
    // GLOBAL CONTROLS: Emergency Stop
    // ========================================================================

    /// <summary>
    /// Triggers global emergency stop - halts all automated operations.
    /// </summary>
    public async Task<EmergencyStopResult> TriggerEmergencyStopAsync(
        string reason,
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _globalEmergencyStop = true;
        }

        // Record the emergency stop
        var stopRecord = new EmergencyStopRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Reason = reason,
            TriggeredBy = triggeredBy,
            TriggeredAt = DateTimeOffset.UtcNow,
            StoppedOperations = new List<string>()
        };

        // Stop all active operations
        var stoppedOps = await _repository.StopAllActiveOperationsAsync(cancellationToken);
        stopRecord.StoppedOperations = stoppedOps.ToList();

        // Pause all integrations
        var pausedIntegrations = await _repository.PauseAllIntegrationsAsync(cancellationToken);

        // Save the stop record
        await _repository.SaveEmergencyStopRecordAsync(stopRecord, cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.SettingsChanged,
            ActorId = triggeredBy,
            ResourceType = "EmergencyStop",
            Details = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["stoppedOperations"] = stoppedOps.Count,
                ["pausedIntegrations"] = pausedIntegrations.Count
            }
        }, cancellationToken);

        OnEmergencyStopTriggered(stopRecord);

        return new EmergencyStopResult(
            Success: true,
            StopId: stopRecord.Id,
            StoppedOperations: stoppedOps,
            PausedIntegrations: pausedIntegrations,
            Message: $"Emergency stop activated. {stoppedOps.Count} operations stopped, " +
                    $"{pausedIntegrations.Count} integrations paused.",
            ResumeInstructions: "Use ResumeFromEmergencyStopAsync to restore operations after review.");
    }

    /// <summary>
    /// Resumes operations after emergency stop (requires explicit confirmation).
    /// </summary>
    public async Task<ResumeResult> ResumeFromEmergencyStopAsync(
        string resumedBy,
        ResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!_globalEmergencyStop)
        {
            return new ResumeResult(
                Success: false,
                Message: "No active emergency stop to resume from");
        }

        if (!options.ConfirmResumeAfterReview)
        {
            return new ResumeResult(
                Success: false,
                Message: "You must confirm you have reviewed the situation before resuming",
                RequiresConfirmation: true);
        }

        lock (_lock)
        {
            _globalEmergencyStop = false;
        }

        var resumedIntegrations = new List<string>();
        var resumedOperations = new List<string>();

        // Resume integrations if requested
        if (options.ResumeIntegrations)
        {
            resumedIntegrations = await _repository.ResumeIntegrationsAsync(
                options.IntegrationsToResume, cancellationToken);
        }

        // Clear paused operations
        if (options.ResumePendingOperations)
        {
            resumedOperations = await _repository.ResumePendingOperationsAsync(cancellationToken);
        }

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.SettingsChanged,
            ActorId = resumedBy,
            ResourceType = "EmergencyStop",
            Details = new Dictionary<string, object>
            {
                ["action"] = "resume",
                ["resumedIntegrations"] = resumedIntegrations.Count,
                ["resumedOperations"] = resumedOperations.Count
            }
        }, cancellationToken);

        return new ResumeResult(
            Success: true,
            Message: $"Operations resumed. {resumedIntegrations.Count} integrations restored, " +
                    $"{resumedOperations.Count} operations resumed.",
            ResumedIntegrations: resumedIntegrations,
            ResumedOperations: resumedOperations);
    }

    /// <summary>
    /// Gets current emergency stop status.
    /// </summary>
    public async Task<EmergencyStopStatus> GetEmergencyStopStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var isActive = _globalEmergencyStop;
        var lastStop = await _repository.GetLastEmergencyStopAsync(cancellationToken);

        return new EmergencyStopStatus(
            IsActive: isActive,
            LastStop: lastStop != null ? new EmergencyStopInfo(
                Id: lastStop.Id,
                Reason: lastStop.Reason,
                TriggeredBy: lastStop.TriggeredBy,
                TriggeredAt: lastStop.TriggeredAt,
                StoppedOperationsCount: lastStop.StoppedOperations.Count) : null,
            ActiveSince: isActive ? lastStop?.TriggeredAt : null);
    }

    // ========================================================================
    // PER-INTEGRATION PAUSE
    // ========================================================================

    /// <summary>
    /// Pauses a specific integration.
    /// </summary>
    public async Task<IntegrationPauseResult> PauseIntegrationAsync(
        string integrationId,
        string reason,
        string pausedBy,
        PauseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new PauseOptions();

        lock (_lock)
        {
            _integrationPaused[integrationId] = true;
        }

        // Stop active operations for this integration
        var stoppedOps = await _repository.StopIntegrationOperationsAsync(
            integrationId, cancellationToken);

        // Queue pending operations if not discarding
        if (!opts.DiscardPendingOperations)
        {
            await _repository.QueuePendingOperationsAsync(integrationId, cancellationToken);
        }

        // Record the pause
        var pauseRecord = new IntegrationPauseRecord
        {
            IntegrationId = integrationId,
            Reason = reason,
            PausedBy = pausedBy,
            PausedAt = DateTimeOffset.UtcNow,
            AutoResumeAt = opts.AutoResumeAfter.HasValue
                ? DateTimeOffset.UtcNow.Add(opts.AutoResumeAfter.Value)
                : null
        };

        await _repository.SaveIntegrationPauseAsync(pauseRecord, cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.IntegrationDisconnected,
            ActorId = pausedBy,
            ResourceId = integrationId,
            ResourceType = "Integration",
            Details = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["stoppedOperations"] = stoppedOps.Count,
                ["autoResumeAt"] = pauseRecord.AutoResumeAt?.ToString() ?? "manual"
            }
        }, cancellationToken);

        OnIntegrationPaused(integrationId, reason);

        return new IntegrationPauseResult(
            Success: true,
            IntegrationId: integrationId,
            StoppedOperations: stoppedOps,
            AutoResumeAt: pauseRecord.AutoResumeAt,
            Message: $"Integration paused. {stoppedOps.Count} operations stopped.");
    }

    /// <summary>
    /// Resumes a paused integration.
    /// </summary>
    public async Task<IntegrationResumeResult> ResumeIntegrationAsync(
        string integrationId,
        string resumedBy,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _integrationPaused[integrationId] = false;
        }

        // Resume queued operations
        var resumedOps = await _repository.ResumeIntegrationOperationsAsync(
            integrationId, cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.IntegrationConnected,
            ActorId = resumedBy,
            ResourceId = integrationId,
            ResourceType = "Integration",
            Details = new Dictionary<string, object>
            {
                ["resumedOperations"] = resumedOps.Count
            }
        }, cancellationToken);

        return new IntegrationResumeResult(
            Success: true,
            IntegrationId: integrationId,
            ResumedOperations: resumedOps,
            Message: $"Integration resumed. {resumedOps.Count} queued operations restarted.");
    }

    /// <summary>
    /// Gets pause status for all integrations.
    /// </summary>
    public async Task<IReadOnlyList<IntegrationPauseStatus>> GetPausedIntegrationsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetPausedIntegrationsAsync(cancellationToken);
    }

    /// <summary>
    /// Checks if an operation should proceed based on kill switches.
    /// </summary>
    public bool CanProceed(string? integrationId = null)
    {
        if (_globalEmergencyStop) return false;

        if (integrationId != null)
        {
            lock (_lock)
            {
                if (_integrationPaused.TryGetValue(integrationId, out var paused) && paused)
                    return false;
            }
        }

        return true;
    }

    // ========================================================================
    // RECOVERY: Safe Mode
    // ========================================================================

    /// <summary>
    /// Starts the app in safe mode with minimal functionality.
    /// </summary>
    public async Task<SafeModeResult> EnterSafeModeAsync(
        string reason,
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        var safeModeConfig = new SafeModeConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Reason = reason,
            TriggeredBy = triggeredBy,
            EnteredAt = DateTimeOffset.UtcNow,
            DisabledFeatures = [
                "scheduled_runs",
                "webhooks",
                "auto_sync",
                "background_jobs",
                "integrations"
            ],
            EnabledFeatures = [
                "view_dashboards",
                "view_history",
                "manual_runs",
                "settings",
                "credential_management"
            ]
        };

        await _repository.SaveSafeModeConfigAsync(safeModeConfig, cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.SettingsChanged,
            ActorId = triggeredBy,
            ResourceType = "SafeMode",
            Details = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["disabledFeatures"] = safeModeConfig.DisabledFeatures.Count
            }
        }, cancellationToken);

        OnSafeModeActivated(safeModeConfig);

        return new SafeModeResult(
            Success: true,
            SafeModeId: safeModeConfig.Id,
            DisabledFeatures: safeModeConfig.DisabledFeatures,
            EnabledFeatures: safeModeConfig.EnabledFeatures,
            Message: "Safe mode activated. Background operations disabled, manual operations available.",
            ExitInstructions: "Review and fix the issue, then call ExitSafeModeAsync to restore full functionality.");
    }

    /// <summary>
    /// Exits safe mode and restores full functionality.
    /// </summary>
    public async Task<SafeModeExitResult> ExitSafeModeAsync(
        string exitedBy,
        bool confirmSystemHealthy,
        CancellationToken cancellationToken = default)
    {
        if (!confirmSystemHealthy)
        {
            return new SafeModeExitResult(
                Success: false,
                Message: "You must confirm the system is healthy before exiting safe mode",
                RequiresConfirmation: true);
        }

        await _repository.ClearSafeModeAsync(cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.SettingsChanged,
            ActorId = exitedBy,
            ResourceType = "SafeMode",
            Details = new Dictionary<string, object>
            {
                ["action"] = "exit",
                ["confirmedHealthy"] = true
            }
        }, cancellationToken);

        return new SafeModeExitResult(
            Success: true,
            Message: "Safe mode exited. Full functionality restored.");
    }

    /// <summary>
    /// Checks if the app is in safe mode.
    /// </summary>
    public async Task<SafeModeStatus> GetSafeModeStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var config = await _repository.GetSafeModeConfigAsync(cancellationToken);

        return new SafeModeStatus(
            IsActive: config != null,
            Config: config,
            AvailableFeatures: config?.EnabledFeatures ?? [],
            DisabledFeatures: config?.DisabledFeatures ?? []);
    }

    // ========================================================================
    // RECOVERY: Credential Revocation
    // ========================================================================

    /// <summary>
    /// Revokes all credentials for an integration (emergency credential reset).
    /// </summary>
    public async Task<CredentialRevocationResult> RevokeCredentialsAsync(
        string integrationId,
        string reason,
        string revokedBy,
        CancellationToken cancellationToken = default)
    {
        // Pause the integration first
        await PauseIntegrationAsync(integrationId, "Credential revocation", revokedBy,
            new PauseOptions { DiscardPendingOperations = false }, cancellationToken);

        // Revoke all stored credentials
        var revokedCredentials = await _repository.RevokeCredentialsAsync(
            integrationId, cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.PermissionRevoked,
            ActorId = revokedBy,
            ResourceId = integrationId,
            ResourceType = "Credentials",
            Details = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["revokedCount"] = revokedCredentials.Count
            }
        }, cancellationToken);

        return new CredentialRevocationResult(
            Success: true,
            IntegrationId: integrationId,
            RevokedCredentials: revokedCredentials,
            Message: $"Revoked {revokedCredentials.Count} credentials. Integration paused.",
            ReconnectInstructions: "Reconnect the integration with new credentials when ready.");
    }

    /// <summary>
    /// Revokes all credentials across all integrations (full reset).
    /// </summary>
    public async Task<GlobalRevocationResult> RevokeAllCredentialsAsync(
        string reason,
        string revokedBy,
        bool confirmDangerousAction,
        CancellationToken cancellationToken = default)
    {
        if (!confirmDangerousAction)
        {
            return new GlobalRevocationResult(
                Success: false,
                Message: "This action will revoke ALL stored credentials. You must explicitly confirm.",
                RequiresConfirmation: true,
                RevokedIntegrations: []);
        }

        // Trigger emergency stop first
        await TriggerEmergencyStopAsync("Global credential revocation", revokedBy, cancellationToken);

        // Revoke all credentials
        var revokedIntegrations = await _repository.RevokeAllCredentialsAsync(cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.PermissionRevoked,
            ActorId = revokedBy,
            ResourceType = "AllCredentials",
            Details = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["revokedIntegrations"] = revokedIntegrations.Count
            }
        }, cancellationToken);

        return new GlobalRevocationResult(
            Success: true,
            Message: $"Revoked credentials for {revokedIntegrations.Count} integrations. " +
                    "All operations stopped. Reconnect integrations when ready.",
            RevokedIntegrations: revokedIntegrations);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void OnEmergencyStopTriggered(EmergencyStopRecord record)
    {
        EmergencyStopTriggered?.Invoke(this, new EmergencyStopEventArgs(record));
    }

    private void OnIntegrationPaused(string integrationId, string reason)
    {
        IntegrationPaused?.Invoke(this, new IntegrationPausedEventArgs(integrationId, reason));
    }

    private void OnSafeModeActivated(SafeModeConfig config)
    {
        SafeModeActivated?.Invoke(this, new SafeModeEventArgs(config));
    }
}

// ============================================================================
// Kill Switch Types
// ============================================================================

public sealed record EmergencyStopResult(
    bool Success,
    string StopId,
    IReadOnlyList<string> StoppedOperations,
    IReadOnlyList<string> PausedIntegrations,
    string Message,
    string ResumeInstructions);

public sealed class EmergencyStopRecord
{
    public required string Id { get; set; }
    public required string Reason { get; set; }
    public required string TriggeredBy { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public List<string> StoppedOperations { get; set; } = new();
}

public sealed record EmergencyStopStatus(
    bool IsActive,
    EmergencyStopInfo? LastStop,
    DateTimeOffset? ActiveSince);

public sealed record EmergencyStopInfo(
    string Id,
    string Reason,
    string TriggeredBy,
    DateTimeOffset TriggeredAt,
    int StoppedOperationsCount);

public sealed record ResumeOptions(
    bool ConfirmResumeAfterReview = false,
    bool ResumeIntegrations = true,
    bool ResumePendingOperations = true,
    IReadOnlyList<string>? IntegrationsToResume = null);

public sealed record ResumeResult(
    bool Success,
    string Message,
    bool RequiresConfirmation = false,
    IReadOnlyList<string>? ResumedIntegrations = null,
    IReadOnlyList<string>? ResumedOperations = null);

public sealed record PauseOptions(
    bool DiscardPendingOperations = false,
    TimeSpan? AutoResumeAfter = null);

public sealed class IntegrationPauseRecord
{
    public required string IntegrationId { get; set; }
    public required string Reason { get; set; }
    public required string PausedBy { get; set; }
    public DateTimeOffset PausedAt { get; set; }
    public DateTimeOffset? AutoResumeAt { get; set; }
}

public sealed record IntegrationPauseResult(
    bool Success,
    string IntegrationId,
    IReadOnlyList<string> StoppedOperations,
    DateTimeOffset? AutoResumeAt,
    string Message);

public sealed record IntegrationResumeResult(
    bool Success,
    string IntegrationId,
    IReadOnlyList<string> ResumedOperations,
    string Message);

public sealed record IntegrationPauseStatus(
    string IntegrationId,
    string IntegrationName,
    bool IsPaused,
    string? PauseReason,
    DateTimeOffset? PausedAt,
    string? PausedBy,
    DateTimeOffset? AutoResumeAt);

public sealed class SafeModeConfig
{
    public required string Id { get; set; }
    public required string Reason { get; set; }
    public required string TriggeredBy { get; set; }
    public DateTimeOffset EnteredAt { get; set; }
    public List<string> DisabledFeatures { get; set; } = new();
    public List<string> EnabledFeatures { get; set; } = new();
}

public sealed record SafeModeResult(
    bool Success,
    string SafeModeId,
    IReadOnlyList<string> DisabledFeatures,
    IReadOnlyList<string> EnabledFeatures,
    string Message,
    string ExitInstructions);

public sealed record SafeModeExitResult(
    bool Success,
    string Message,
    bool RequiresConfirmation = false);

public sealed record SafeModeStatus(
    bool IsActive,
    SafeModeConfig? Config,
    IReadOnlyList<string> AvailableFeatures,
    IReadOnlyList<string> DisabledFeatures);

public sealed record CredentialRevocationResult(
    bool Success,
    string IntegrationId,
    IReadOnlyList<string> RevokedCredentials,
    string Message,
    string ReconnectInstructions);

public sealed record GlobalRevocationResult(
    bool Success,
    string Message,
    IReadOnlyList<string> RevokedIntegrations,
    bool RequiresConfirmation = false);

// Events
public sealed class EmergencyStopEventArgs : EventArgs
{
    public EmergencyStopRecord Record { get; }
    public EmergencyStopEventArgs(EmergencyStopRecord record) => Record = record;
}

public sealed class IntegrationPausedEventArgs : EventArgs
{
    public string IntegrationId { get; }
    public string Reason { get; }
    public IntegrationPausedEventArgs(string integrationId, string reason)
    {
        IntegrationId = integrationId;
        Reason = reason;
    }
}

public sealed class SafeModeEventArgs : EventArgs
{
    public SafeModeConfig Config { get; }
    public SafeModeEventArgs(SafeModeConfig config) => Config = config;
}

// Interfaces
public interface IKillSwitchRepository
{
    Task<IReadOnlyList<string>> StopAllActiveOperationsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> PauseAllIntegrationsAsync(CancellationToken cancellationToken);
    Task SaveEmergencyStopRecordAsync(EmergencyStopRecord record, CancellationToken cancellationToken);
    Task<EmergencyStopRecord?> GetLastEmergencyStopAsync(CancellationToken cancellationToken);
    Task<List<string>> ResumeIntegrationsAsync(IReadOnlyList<string>? integrations, CancellationToken cancellationToken);
    Task<List<string>> ResumePendingOperationsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> StopIntegrationOperationsAsync(string integrationId, CancellationToken cancellationToken);
    Task QueuePendingOperationsAsync(string integrationId, CancellationToken cancellationToken);
    Task SaveIntegrationPauseAsync(IntegrationPauseRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ResumeIntegrationOperationsAsync(string integrationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<IntegrationPauseStatus>> GetPausedIntegrationsAsync(CancellationToken cancellationToken);
    Task SaveSafeModeConfigAsync(SafeModeConfig config, CancellationToken cancellationToken);
    Task<SafeModeConfig?> GetSafeModeConfigAsync(CancellationToken cancellationToken);
    Task ClearSafeModeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> RevokeCredentialsAsync(string integrationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> RevokeAllCredentialsAsync(CancellationToken cancellationToken);
}
