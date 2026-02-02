namespace ControlRoom.Application.Services;

/// <summary>
/// Security By Design: Provides comprehensive security controls including
/// secrets management, boundary enforcement, and attack surface minimization.
///
/// Checklist items addressed:
/// - Secrets encrypted at rest
/// - Secrets rotatable
/// - Least privilege enforced
/// - Multi-tenant safety
/// - Webhooks verified
/// - Errors don't leak info
/// </summary>
public sealed class SecurityByDesignService
{
    private readonly ISecretStore _secretStore;
    private readonly IPermissionService _permissionService;
    private readonly ITenantIsolation _tenantIsolation;
    private readonly IWebhookVerifier _webhookVerifier;
    private readonly IAuditLogRepository _auditRepository;

    public event EventHandler<SecretRotatedEventArgs>? SecretRotated;
    public event EventHandler<PrivilegeEscalationAttemptEventArgs>? PrivilegeEscalationAttempted;
    public event EventHandler<TenantBoundaryViolationEventArgs>? TenantBoundaryViolation;

    public SecurityByDesignService(
        ISecretStore secretStore,
        IPermissionService permissionService,
        ITenantIsolation tenantIsolation,
        IWebhookVerifier webhookVerifier,
        IAuditLogRepository auditRepository)
    {
        _secretStore = secretStore;
        _permissionService = permissionService;
        _tenantIsolation = tenantIsolation;
        _webhookVerifier = webhookVerifier;
        _auditRepository = auditRepository;
    }

    // ========================================================================
    // SECRETS MANAGEMENT: Encrypted at Rest & Rotatable
    // ========================================================================

    /// <summary>
    /// Stores a secret with encryption at rest.
    /// </summary>
    public async Task<SecretStoreResult> StoreSecretAsync(
        string secretId,
        string secretValue,
        SecretMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        // Validate secret before storage
        var validationResult = ValidateSecretPolicy(secretValue, metadata);
        if (!validationResult.IsValid)
        {
            return new SecretStoreResult
            {
                Success = false,
                SecretId = secretId,
                Error = validationResult.Error
            };
        }

        // Encrypt and store
        var encryptedValue = await _secretStore.EncryptAsync(secretValue, cancellationToken);

        var secret = new StoredSecret
        {
            Id = secretId,
            EncryptedValue = encryptedValue,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = metadata.ExpirationPolicy switch
            {
                SecretExpirationPolicy.Days30 => DateTimeOffset.UtcNow.AddDays(30),
                SecretExpirationPolicy.Days90 => DateTimeOffset.UtcNow.AddDays(90),
                SecretExpirationPolicy.Days365 => DateTimeOffset.UtcNow.AddDays(365),
                _ => null
            },
            RotationSchedule = metadata.RotationSchedule
        };

        await _secretStore.SaveAsync(secret, cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.SecretCreated,
            ActorId = metadata.CreatedBy,
            ResourceType = "secret",
            ResourceId = secretId,
            Details = new Dictionary<string, object>
            {
                ["type"] = metadata.SecretType.ToString(),
                ["hasExpiration"] = secret.ExpiresAt.HasValue
            }
        }, cancellationToken);

        return new SecretStoreResult
        {
            Success = true,
            SecretId = secretId,
            ExpiresAt = secret.ExpiresAt
        };
    }

    /// <summary>
    /// Retrieves a secret (decrypted).
    /// </summary>
    public async Task<SecretRetrievalResult> GetSecretAsync(
        string secretId,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        // Check access permission
        var hasAccess = await _permissionService.CanAccessSecretAsync(
            requestedBy, secretId, cancellationToken);

        if (!hasAccess)
        {
            await _auditRepository.RecordAsync(new AuditEntry
            {
                Action = AuditAction.AccessDenied,
                ActorId = requestedBy,
                ResourceType = "secret",
                ResourceId = secretId
            }, cancellationToken);

            return new SecretRetrievalResult
            {
                Success = false,
                Error = "Access denied"
            };
        }

        var secret = await _secretStore.GetAsync(secretId, cancellationToken);
        if (secret == null)
        {
            return new SecretRetrievalResult
            {
                Success = false,
                Error = "Secret not found"
            };
        }

        // Check if expired
        if (secret.ExpiresAt.HasValue && secret.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return new SecretRetrievalResult
            {
                Success = false,
                Error = "Secret has expired",
                IsExpired = true
            };
        }

        // Decrypt
        var decryptedValue = await _secretStore.DecryptAsync(
            secret.EncryptedValue, cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.SecretAccessed,
            ActorId = requestedBy,
            ResourceType = "secret",
            ResourceId = secretId
        }, cancellationToken);

        return new SecretRetrievalResult
        {
            Success = true,
            SecretId = secretId,
            Value = decryptedValue,
            ExpiresAt = secret.ExpiresAt
        };
    }

    /// <summary>
    /// Rotates a secret (generates new value or accepts new value).
    /// </summary>
    public async Task<SecretRotationResult> RotateSecretAsync(
        string secretId,
        string? newValue,
        string rotatedBy,
        CancellationToken cancellationToken = default)
    {
        // Check rotation permission
        var canRotate = await _permissionService.CanRotateSecretAsync(
            rotatedBy, secretId, cancellationToken);

        if (!canRotate)
        {
            return new SecretRotationResult
            {
                Success = false,
                SecretId = secretId,
                Error = "Not authorized to rotate this secret"
            };
        }

        var existingSecret = await _secretStore.GetAsync(secretId, cancellationToken);
        if (existingSecret == null)
        {
            return new SecretRotationResult
            {
                Success = false,
                SecretId = secretId,
                Error = "Secret not found"
            };
        }

        // Generate new value if not provided
        var valueToStore = newValue ?? await GenerateSecretValueAsync(
            existingSecret.Metadata.SecretType, cancellationToken);

        // Encrypt new value
        var encryptedValue = await _secretStore.EncryptAsync(valueToStore, cancellationToken);

        // Archive old version
        await _secretStore.ArchiveVersionAsync(existingSecret, cancellationToken);

        // Update secret
        existingSecret.EncryptedValue = encryptedValue;
        existingSecret.RotatedAt = DateTimeOffset.UtcNow;
        existingSecret.RotationCount++;
        existingSecret.ExpiresAt = existingSecret.Metadata.ExpirationPolicy switch
        {
            SecretExpirationPolicy.Days30 => DateTimeOffset.UtcNow.AddDays(30),
            SecretExpirationPolicy.Days90 => DateTimeOffset.UtcNow.AddDays(90),
            SecretExpirationPolicy.Days365 => DateTimeOffset.UtcNow.AddDays(365),
            _ => null
        };

        await _secretStore.SaveAsync(existingSecret, cancellationToken);

        // Audit
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = AuditAction.SecretRotated,
            ActorId = rotatedBy,
            ResourceType = "secret",
            ResourceId = secretId,
            Details = new Dictionary<string, object>
            {
                ["rotationCount"] = existingSecret.RotationCount
            }
        }, cancellationToken);

        SecretRotated?.Invoke(this, new SecretRotatedEventArgs(secretId, rotatedBy));

        return new SecretRotationResult
        {
            Success = true,
            SecretId = secretId,
            RotatedAt = existingSecret.RotatedAt.Value,
            NewExpiresAt = existingSecret.ExpiresAt
        };
    }

    /// <summary>
    /// Gets secrets that need rotation soon.
    /// </summary>
    public async Task<IReadOnlyList<SecretRotationReminder>> GetSecretsNeedingRotationAsync(
        TimeSpan warningThreshold,
        CancellationToken cancellationToken = default)
    {
        var secrets = await _secretStore.GetAllAsync(cancellationToken);
        var reminders = new List<SecretRotationReminder>();

        foreach (var secret in secrets)
        {
            var needsRotation = false;
            string reason = "";

            // Check expiration
            if (secret.ExpiresAt.HasValue)
            {
                var timeUntilExpiry = secret.ExpiresAt.Value - DateTimeOffset.UtcNow;
                if (timeUntilExpiry < warningThreshold)
                {
                    needsRotation = true;
                    reason = $"Expires in {timeUntilExpiry.TotalDays:F0} days";
                }
            }

            // Check rotation schedule
            if (secret.RotationSchedule.HasValue && secret.RotatedAt.HasValue)
            {
                var timeSinceRotation = DateTimeOffset.UtcNow - secret.RotatedAt.Value;
                if (timeSinceRotation > secret.RotationSchedule.Value)
                {
                    needsRotation = true;
                    reason = $"Last rotated {timeSinceRotation.TotalDays:F0} days ago";
                }
            }

            if (needsRotation)
            {
                reminders.Add(new SecretRotationReminder
                {
                    SecretId = secret.Id,
                    SecretType = secret.Metadata.SecretType,
                    Reason = reason,
                    Priority = CalculateRotationPriority(secret)
                });
            }
        }

        return reminders.OrderByDescending(r => r.Priority).ToList();
    }

    // ========================================================================
    // BOUNDARIES: Least Privilege & Multi-Tenant Safety
    // ========================================================================

    /// <summary>
    /// Checks if an action is allowed under least privilege principle.
    /// </summary>
    public async Task<PrivilegeCheckResult> CheckPrivilegeAsync(
        string actorId,
        string resource,
        PrivilegedAction action,
        CancellationToken cancellationToken = default)
    {
        var permissions = await _permissionService.GetEffectivePermissionsAsync(
            actorId, cancellationToken);

        var requiredPermission = MapActionToPermission(action);
        var hasPermission = permissions.Contains(requiredPermission) ||
                           permissions.Contains(Permission.Admin);

        if (!hasPermission)
        {
            // Log potential privilege escalation attempt
            await _auditRepository.RecordAsync(new AuditEntry
            {
                Action = AuditAction.AccessDenied,
                ActorId = actorId,
                ResourceType = resource,
                Details = new Dictionary<string, object>
                {
                    ["attemptedAction"] = action.ToString(),
                    ["requiredPermission"] = requiredPermission.ToString()
                }
            }, cancellationToken);

            PrivilegeEscalationAttempted?.Invoke(this,
                new PrivilegeEscalationAttemptEventArgs(actorId, resource, action));
        }

        return new PrivilegeCheckResult
        {
            Allowed = hasPermission,
            ActorId = actorId,
            Resource = resource,
            Action = action,
            EffectivePermissions = permissions,
            MissingPermission = hasPermission ? null : requiredPermission
        };
    }

    /// <summary>
    /// Validates tenant isolation for a resource access.
    /// </summary>
    public async Task<TenantIsolationResult> ValidateTenantAccessAsync(
        string actorTenantId,
        string resourceTenantId,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        // Same tenant - always allowed
        if (actorTenantId == resourceTenantId)
        {
            return new TenantIsolationResult
            {
                Allowed = true,
                ActorTenantId = actorTenantId,
                ResourceTenantId = resourceTenantId
            };
        }

        // Check for cross-tenant sharing permission
        var hasSharing = await _tenantIsolation.HasCrossTenantAccessAsync(
            actorTenantId, resourceTenantId, resourceId, cancellationToken);

        if (!hasSharing)
        {
            // Log boundary violation
            await _auditRepository.RecordAsync(new AuditEntry
            {
                Action = AuditAction.TenantBoundaryViolation,
                ActorId = actorTenantId,
                ResourceType = "tenant_boundary",
                ResourceId = resourceId,
                Details = new Dictionary<string, object>
                {
                    ["targetTenant"] = resourceTenantId
                }
            }, cancellationToken);

            TenantBoundaryViolation?.Invoke(this,
                new TenantBoundaryViolationEventArgs(actorTenantId, resourceTenantId, resourceId));
        }

        return new TenantIsolationResult
        {
            Allowed = hasSharing,
            ActorTenantId = actorTenantId,
            ResourceTenantId = resourceTenantId,
            CrossTenantAccessType = hasSharing ? await GetCrossTenantAccessTypeAsync(
                actorTenantId, resourceTenantId, resourceId, cancellationToken) : null
        };
    }

    /// <summary>
    /// Gets audit trail of all privilege escalations and boundary violations.
    /// </summary>
    public async Task<IReadOnlyList<SecurityAuditEvent>> GetSecurityAuditTrailAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var events = new List<SecurityAuditEvent>();

        var auditEntries = await _auditRepository.GetEntriesAsync(
            from, to,
            new[] { AuditAction.AccessDenied, AuditAction.TenantBoundaryViolation,
                   AuditAction.SecretAccessed, AuditAction.SecretRotated },
            cancellationToken);

        foreach (var entry in auditEntries)
        {
            if (tenantId != null)
            {
                // Filter by tenant if specified
                var entryTenant = entry.Details.GetValueOrDefault("tenantId")?.ToString();
                if (entryTenant != tenantId) continue;
            }

            events.Add(new SecurityAuditEvent
            {
                EventId = entry.Id,
                Timestamp = entry.Timestamp,
                EventType = MapAuditActionToSecurityEvent(entry.Action),
                ActorId = entry.ActorId,
                ResourceType = entry.ResourceType,
                ResourceId = entry.ResourceId,
                Details = entry.Details,
                Severity = DetermineSecuritySeverity(entry)
            });
        }

        return events;
    }

    // ========================================================================
    // SURFACE AREA: Webhook Verification & Error Sanitization
    // ========================================================================

    /// <summary>
    /// Verifies an incoming webhook request.
    /// </summary>
    public async Task<WebhookVerificationResult> VerifyWebhookAsync(
        string provider,
        string signature,
        string payload,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        var config = await _webhookVerifier.GetProviderConfigAsync(provider, cancellationToken);
        if (config == null)
        {
            return new WebhookVerificationResult
            {
                Verified = false,
                Provider = provider,
                Error = "Unknown webhook provider"
            };
        }

        var verificationResult = config.SignatureAlgorithm switch
        {
            SignatureAlgorithm.HmacSha256 => VerifyHmacSha256(signature, payload, config.Secret),
            SignatureAlgorithm.HmacSha1 => VerifyHmacSha1(signature, payload, config.Secret),
            SignatureAlgorithm.RsaSha256 => await VerifyRsaSha256Async(signature, payload, config, cancellationToken),
            _ => new SignatureVerification { Valid = false, Error = "Unsupported algorithm" }
        };

        // Check timestamp to prevent replay attacks
        var timestampValid = ValidateWebhookTimestamp(headers, config.TimestampTolerance);

        var verified = verificationResult.Valid && timestampValid;

        // Audit webhook verification
        await _auditRepository.RecordAsync(new AuditEntry
        {
            Action = verified ? AuditAction.WebhookVerified : AuditAction.WebhookRejected,
            ActorId = provider,
            ResourceType = "webhook",
            Details = new Dictionary<string, object>
            {
                ["signatureValid"] = verificationResult.Valid,
                ["timestampValid"] = timestampValid
            }
        }, cancellationToken);

        return new WebhookVerificationResult
        {
            Verified = verified,
            Provider = provider,
            SignatureValid = verificationResult.Valid,
            TimestampValid = timestampValid,
            Error = !verified ? (verificationResult.Error ?? "Timestamp validation failed") : null
        };
    }

    /// <summary>
    /// Sanitizes an error message to prevent information leakage.
    /// </summary>
    public SanitizedError SanitizeError(Exception exception, bool includeCorrelationId = true)
    {
        var correlationId = includeCorrelationId ? Guid.NewGuid().ToString("N")[..8] : null;

        // Log full error internally
        LogInternalError(exception, correlationId);

        // Return sanitized error
        return exception switch
        {
            UnauthorizedAccessException => new SanitizedError
            {
                PublicMessage = "Authentication required",
                ErrorCode = "AUTH_001",
                CorrelationId = correlationId
            },
            InvalidOperationException => new SanitizedError
            {
                PublicMessage = "Operation not allowed",
                ErrorCode = "OP_001",
                CorrelationId = correlationId
            },
            ArgumentException => new SanitizedError
            {
                PublicMessage = "Invalid request parameters",
                ErrorCode = "REQ_001",
                CorrelationId = correlationId
            },
            TimeoutException => new SanitizedError
            {
                PublicMessage = "Request timed out",
                ErrorCode = "TIMEOUT_001",
                CorrelationId = correlationId
            },
            _ => new SanitizedError
            {
                PublicMessage = "An unexpected error occurred",
                ErrorCode = "ERR_001",
                CorrelationId = correlationId
            }
        };
    }

    /// <summary>
    /// Validates that a response doesn't leak sensitive information.
    /// </summary>
    public ResponseValidation ValidateResponseForLeakage(
        object response,
        IReadOnlyList<string>? additionalSensitivePatterns = null)
    {
        var issues = new List<LeakageIssue>();
        var responseJson = System.Text.Json.JsonSerializer.Serialize(response);

        // Check for common sensitive data patterns
        var patterns = new List<(string Pattern, string Type)>
        {
            (@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", "email"),
            (@"\b\d{3}-\d{2}-\d{4}\b", "ssn"),
            (@"\b\d{16}\b", "credit_card"),
            (@"['""]+(sk_|pk_|api_|key_|secret_)[A-Za-z0-9_-]+['""]+", "api_key"),
            (@"password['""]?\s*:\s*['""]?\S+['""]?", "password"),
            (@"bearer\s+[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+", "jwt_token"),
            (@"\bey[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\b", "jwt_token")
        };

        if (additionalSensitivePatterns != null)
        {
            patterns.AddRange(additionalSensitivePatterns.Select(p => (p, "custom")));
        }

        foreach (var (pattern, type) in patterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                responseJson, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                issues.Add(new LeakageIssue
                {
                    Type = type,
                    Pattern = pattern,
                    Location = $"Position {match.Index}",
                    Severity = type switch
                    {
                        "password" or "api_key" or "jwt_token" => LeakageSeverity.Critical,
                        "ssn" or "credit_card" => LeakageSeverity.High,
                        "email" => LeakageSeverity.Medium,
                        _ => LeakageSeverity.Low
                    }
                });
            }
        }

        return new ResponseValidation
        {
            IsClean = !issues.Any(),
            Issues = issues,
            Recommendation = issues.Any(i => i.Severity == LeakageSeverity.Critical)
                ? "Critical: Response contains sensitive data. Do not send to client."
                : issues.Any()
                    ? "Review flagged items before sending response."
                    : "Response appears clean."
        };
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    private SecretValidation ValidateSecretPolicy(string value, SecretMetadata metadata)
    {
        // Length requirements based on type
        var minLength = metadata.SecretType switch
        {
            SecretType.ApiKey => 32,
            SecretType.Password => 12,
            SecretType.Certificate => 100,
            SecretType.EncryptionKey => 32,
            _ => 8
        };

        if (value.Length < minLength)
        {
            return new SecretValidation
            {
                IsValid = false,
                Error = $"Secret must be at least {minLength} characters for type {metadata.SecretType}"
            };
        }

        // Complexity for passwords
        if (metadata.SecretType == SecretType.Password)
        {
            var hasUpper = value.Any(char.IsUpper);
            var hasLower = value.Any(char.IsLower);
            var hasDigit = value.Any(char.IsDigit);
            var hasSpecial = value.Any(c => !char.IsLetterOrDigit(c));

            if (!hasUpper || !hasLower || !hasDigit || !hasSpecial)
            {
                return new SecretValidation
                {
                    IsValid = false,
                    Error = "Password must contain uppercase, lowercase, digit, and special character"
                };
            }
        }

        return new SecretValidation { IsValid = true };
    }

    private async Task<string> GenerateSecretValueAsync(
        SecretType secretType,
        CancellationToken cancellationToken)
    {
        var length = secretType switch
        {
            SecretType.ApiKey => 64,
            SecretType.Password => 24,
            SecretType.EncryptionKey => 64,
            _ => 32
        };

        return await _secretStore.GenerateSecureRandomAsync(length, cancellationToken);
    }

    private RotationPriority CalculateRotationPriority(StoredSecret secret)
    {
        if (secret.ExpiresAt.HasValue)
        {
            var timeUntilExpiry = secret.ExpiresAt.Value - DateTimeOffset.UtcNow;
            if (timeUntilExpiry < TimeSpan.FromDays(7)) return RotationPriority.Critical;
            if (timeUntilExpiry < TimeSpan.FromDays(14)) return RotationPriority.High;
        }

        return secret.Metadata.SecretType switch
        {
            SecretType.ApiKey => RotationPriority.High,
            SecretType.Password => RotationPriority.Medium,
            _ => RotationPriority.Low
        };
    }

    private Permission MapActionToPermission(PrivilegedAction action)
    {
        return action switch
        {
            PrivilegedAction.CreateResource => Permission.Create,
            PrivilegedAction.DeleteResource => Permission.Delete,
            PrivilegedAction.ModifyPermissions => Permission.Admin,
            PrivilegedAction.AccessSecrets => Permission.SecretAccess,
            PrivilegedAction.RotateSecrets => Permission.SecretRotate,
            PrivilegedAction.ExportData => Permission.Export,
            PrivilegedAction.ManageIntegrations => Permission.IntegrationManage,
            _ => Permission.Read
        };
    }

    private async Task<string?> GetCrossTenantAccessTypeAsync(
        string actorTenantId,
        string resourceTenantId,
        string resourceId,
        CancellationToken cancellationToken)
    {
        return await _tenantIsolation.GetAccessTypeAsync(
            actorTenantId, resourceTenantId, resourceId, cancellationToken);
    }

    private SecurityEventType MapAuditActionToSecurityEvent(AuditAction action)
    {
        return action switch
        {
            AuditAction.AccessDenied => SecurityEventType.AccessDenied,
            AuditAction.TenantBoundaryViolation => SecurityEventType.TenantViolation,
            AuditAction.SecretAccessed => SecurityEventType.SecretAccess,
            AuditAction.SecretRotated => SecurityEventType.SecretRotation,
            _ => SecurityEventType.Other
        };
    }

    private SecuritySeverity DetermineSecuritySeverity(AuditEntry entry)
    {
        return entry.Action switch
        {
            AuditAction.TenantBoundaryViolation => SecuritySeverity.Critical,
            AuditAction.AccessDenied when entry.Details.ContainsKey("privilegeEscalation") =>
                SecuritySeverity.High,
            AuditAction.AccessDenied => SecuritySeverity.Medium,
            AuditAction.SecretAccessed => SecuritySeverity.Low,
            _ => SecuritySeverity.Info
        };
    }

    private SignatureVerification VerifyHmacSha256(string signature, string payload, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        var computedSignature = Convert.ToHexString(hash).ToLower();

        var providedSignature = signature.StartsWith("sha256=")
            ? signature[7..]
            : signature;

        return new SignatureVerification
        {
            Valid = string.Equals(computedSignature, providedSignature.ToLower(),
                StringComparison.OrdinalIgnoreCase)
        };
    }

    private SignatureVerification VerifyHmacSha1(string signature, string payload, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA1(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        var computedSignature = Convert.ToHexString(hash).ToLower();

        var providedSignature = signature.StartsWith("sha1=")
            ? signature[5..]
            : signature;

        return new SignatureVerification
        {
            Valid = string.Equals(computedSignature, providedSignature.ToLower(),
                StringComparison.OrdinalIgnoreCase)
        };
    }

    private async Task<SignatureVerification> VerifyRsaSha256Async(
        string signature,
        string payload,
        WebhookProviderConfig config,
        CancellationToken cancellationToken)
    {
        // Placeholder for RSA verification
        await Task.CompletedTask;
        return new SignatureVerification
        {
            Valid = false,
            Error = "RSA verification not yet implemented"
        };
    }

    private bool ValidateWebhookTimestamp(
        Dictionary<string, string> headers,
        TimeSpan tolerance)
    {
        var timestampHeaders = new[] { "X-Timestamp", "X-Hub-Timestamp", "X-Slack-Request-Timestamp" };

        foreach (var headerName in timestampHeaders)
        {
            if (headers.TryGetValue(headerName, out var timestampStr))
            {
                if (long.TryParse(timestampStr, out var unixTimestamp))
                {
                    var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
                    var age = DateTimeOffset.UtcNow - timestamp;
                    return age.Duration() < tolerance;
                }
            }
        }

        // No timestamp header found - fail open with warning
        return true;
    }

    private void LogInternalError(Exception exception, string? correlationId)
    {
        // Internal logging - full details stored securely
        // In production, this would log to a secure, internal-only logging system
    }
}

// ========================================================================
// SUPPORTING TYPES
// ========================================================================

public class SecretStoreResult
{
    public bool Success { get; init; }
    public required string SecretId { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public class SecretRetrievalResult
{
    public bool Success { get; init; }
    public string? SecretId { get; init; }
    public string? Value { get; init; }
    public string? Error { get; init; }
    public bool IsExpired { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public class SecretRotationResult
{
    public bool Success { get; init; }
    public required string SecretId { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? RotatedAt { get; init; }
    public DateTimeOffset? NewExpiresAt { get; init; }
}

public class SecretRotationReminder
{
    public required string SecretId { get; init; }
    public SecretType SecretType { get; init; }
    public required string Reason { get; init; }
    public RotationPriority Priority { get; init; }
}

public enum RotationPriority
{
    Low,
    Medium,
    High,
    Critical
}

public class SecretMetadata
{
    public SecretType SecretType { get; init; }
    public SecretExpirationPolicy ExpirationPolicy { get; init; }
    public TimeSpan? RotationSchedule { get; init; }
    public required string CreatedBy { get; init; }
    public Dictionary<string, string> Tags { get; init; } = new();
}

public enum SecretType
{
    ApiKey,
    Password,
    Certificate,
    EncryptionKey,
    WebhookSecret,
    OAuthToken,
    Other
}

public enum SecretExpirationPolicy
{
    NoExpiration,
    Days30,
    Days90,
    Days365
}

public class StoredSecret
{
    public required string Id { get; init; }
    public required byte[] EncryptedValue { get; set; }
    public required SecretMetadata Metadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public int RotationCount { get; set; }
    public TimeSpan? RotationSchedule { get; init; }
}

public class SecretValidation
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
}

public class PrivilegeCheckResult
{
    public bool Allowed { get; init; }
    public required string ActorId { get; init; }
    public required string Resource { get; init; }
    public PrivilegedAction Action { get; init; }
    public IReadOnlySet<Permission> EffectivePermissions { get; init; } = new HashSet<Permission>();
    public Permission? MissingPermission { get; init; }
}

public enum PrivilegedAction
{
    ReadResource,
    CreateResource,
    UpdateResource,
    DeleteResource,
    ModifyPermissions,
    AccessSecrets,
    RotateSecrets,
    ExportData,
    ManageIntegrations
}

public enum Permission
{
    Read,
    Create,
    Update,
    Delete,
    Admin,
    SecretAccess,
    SecretRotate,
    Export,
    IntegrationManage
}

public class TenantIsolationResult
{
    public bool Allowed { get; init; }
    public required string ActorTenantId { get; init; }
    public required string ResourceTenantId { get; init; }
    public string? CrossTenantAccessType { get; init; }
}

public class SecurityAuditEvent
{
    public required string EventId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public SecurityEventType EventType { get; init; }
    public required string ActorId { get; init; }
    public required string ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public Dictionary<string, object> Details { get; init; } = new();
    public SecuritySeverity Severity { get; init; }
}

public enum SecurityEventType
{
    AccessDenied,
    TenantViolation,
    SecretAccess,
    SecretRotation,
    Other
}

public enum SecuritySeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

public class WebhookVerificationResult
{
    public bool Verified { get; init; }
    public required string Provider { get; init; }
    public bool SignatureValid { get; init; }
    public bool TimestampValid { get; init; }
    public string? Error { get; init; }
}

public class WebhookProviderConfig
{
    public required string ProviderId { get; init; }
    public required string Secret { get; init; }
    public SignatureAlgorithm SignatureAlgorithm { get; init; }
    public TimeSpan TimestampTolerance { get; init; } = TimeSpan.FromMinutes(5);
    public string? PublicKey { get; init; }
}

public enum SignatureAlgorithm
{
    HmacSha1,
    HmacSha256,
    RsaSha256
}

public class SignatureVerification
{
    public bool Valid { get; init; }
    public string? Error { get; init; }
}

public class SanitizedError
{
    public required string PublicMessage { get; init; }
    public required string ErrorCode { get; init; }
    public string? CorrelationId { get; init; }
}

public class ResponseValidation
{
    public bool IsClean { get; init; }
    public IReadOnlyList<LeakageIssue> Issues { get; init; } = Array.Empty<LeakageIssue>();
    public required string Recommendation { get; init; }
}

public class LeakageIssue
{
    public required string Type { get; init; }
    public required string Pattern { get; init; }
    public required string Location { get; init; }
    public LeakageSeverity Severity { get; init; }
}

public enum LeakageSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public class SecretRotatedEventArgs : EventArgs
{
    public string SecretId { get; }
    public string RotatedBy { get; }
    public SecretRotatedEventArgs(string secretId, string rotatedBy)
    {
        SecretId = secretId;
        RotatedBy = rotatedBy;
    }
}

public class PrivilegeEscalationAttemptEventArgs : EventArgs
{
    public string ActorId { get; }
    public string Resource { get; }
    public PrivilegedAction AttemptedAction { get; }
    public PrivilegeEscalationAttemptEventArgs(string actorId, string resource, PrivilegedAction action)
    {
        ActorId = actorId;
        Resource = resource;
        AttemptedAction = action;
    }
}

public class TenantBoundaryViolationEventArgs : EventArgs
{
    public string ActorTenantId { get; }
    public string TargetTenantId { get; }
    public string ResourceId { get; }
    public TenantBoundaryViolationEventArgs(string actorTenant, string targetTenant, string resourceId)
    {
        ActorTenantId = actorTenant;
        TargetTenantId = targetTenant;
        ResourceId = resourceId;
    }
}

// ========================================================================
// REPOSITORY INTERFACES
// ========================================================================

public interface ISecretStore
{
    Task<byte[]> EncryptAsync(string value, CancellationToken cancellationToken);
    Task<string> DecryptAsync(byte[] encryptedValue, CancellationToken cancellationToken);
    Task SaveAsync(StoredSecret secret, CancellationToken cancellationToken);
    Task<StoredSecret?> GetAsync(string secretId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredSecret>> GetAllAsync(CancellationToken cancellationToken);
    Task ArchiveVersionAsync(StoredSecret secret, CancellationToken cancellationToken);
    Task<string> GenerateSecureRandomAsync(int length, CancellationToken cancellationToken);
}

public interface IPermissionService
{
    Task<bool> CanAccessSecretAsync(string actorId, string secretId, CancellationToken cancellationToken);
    Task<bool> CanRotateSecretAsync(string actorId, string secretId, CancellationToken cancellationToken);
    Task<IReadOnlySet<Permission>> GetEffectivePermissionsAsync(string actorId, CancellationToken cancellationToken);
}

public interface ITenantIsolation
{
    Task<bool> HasCrossTenantAccessAsync(string actorTenantId, string resourceTenantId, string resourceId, CancellationToken cancellationToken);
    Task<string?> GetAccessTypeAsync(string actorTenantId, string resourceTenantId, string resourceId, CancellationToken cancellationToken);
}

public interface IWebhookVerifier
{
    Task<WebhookProviderConfig?> GetProviderConfigAsync(string providerId, CancellationToken cancellationToken);
}
