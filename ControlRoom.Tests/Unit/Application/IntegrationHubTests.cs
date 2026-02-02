using ControlRoom.Application.UseCases;
using ControlRoom.Domain.Model;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Unit tests for IntegrationHub use case DTOs and events.
/// </summary>
public sealed class IntegrationHubTests
{
    // ========================================================================
    // Event Args Tests
    // ========================================================================

    [Fact]
    public void IntegrationConnectedEventArgs_Properties()
    {
        var instance = CreateTestInstance();
        var args = new IntegrationConnectedEventArgs { Instance = instance };

        Assert.Equal(instance, args.Instance);
    }

    [Fact]
    public void IntegrationDisconnectedEventArgs_Properties()
    {
        var instanceId = IntegrationInstanceId.New();
        var args = new IntegrationDisconnectedEventArgs { InstanceId = instanceId };

        Assert.Equal(instanceId, args.InstanceId);
    }

    [Fact]
    public void WebhookReceivedEventArgs_Properties()
    {
        var webhookId = WebhookId.New();
        var args = new WebhookReceivedEventArgs
        {
            WebhookId = webhookId,
            EventType = WebhookEventType.Push,
            Payload = """{"ref": "refs/heads/main"}"""
        };

        Assert.Equal(webhookId, args.WebhookId);
        Assert.Equal(WebhookEventType.Push, args.EventType);
        Assert.Contains("main", args.Payload);
    }

    [Fact]
    public void SyncCompletedEventArgs_Properties()
    {
        var job = CreateTestSyncJob();
        var args = new SyncCompletedEventArgs
        {
            Job = job,
            Success = true
        };

        Assert.Equal(job, args.Job);
        Assert.True(args.Success);
    }

    // ========================================================================
    // Integration Instance Tests
    // ========================================================================

    [Fact]
    public void IntegrationInstance_AllProperties()
    {
        var instance = CreateTestInstance();

        Assert.NotEqual(default, instance.Id);
        Assert.NotEqual(default, instance.IntegrationId);
        Assert.NotEqual(default, instance.OwnerId);
        Assert.Equal("my-github", instance.Name);
        Assert.Equal("My GitHub Account", instance.DisplayName);
        Assert.Equal(IntegrationStatus.Connected, instance.Status);
        Assert.Equal(IntegrationHealth.Healthy, instance.Health);
    }

    [Fact]
    public void IntegrationInstance_WithTeam()
    {
        var teamId = TeamId.New();
        var instance = new IntegrationInstance(
            IntegrationInstanceId.New(),
            IntegrationId.New(),
            UserId.New(),
            teamId,
            "team-instance",
            "Team Instance",
            IntegrationStatus.Pending,
            IntegrationHealth.Unknown,
            new(),
            null,
            DateTimeOffset.UtcNow,
            null, null, null, null, null
        );

        Assert.Equal(teamId, instance.TeamId);
    }

    [Fact]
    public void IntegrationInstance_WithCredentials()
    {
        var credentials = new CredentialInfo(
            "encrypted-token",
            "encrypted-refresh",
            DateTimeOffset.UtcNow.AddHours(1),
            ["repo", "user"],
            "Bearer",
            new() { ["scope"] = "full" }
        );

        var instance = new IntegrationInstance(
            IntegrationInstanceId.New(),
            IntegrationId.New(),
            UserId.New(),
            null,
            "my-instance",
            "My Instance",
            IntegrationStatus.Connected,
            IntegrationHealth.Healthy,
            new(),
            credentials,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null, null, null, null
        );

        Assert.NotNull(instance.Credentials);
        Assert.Equal("encrypted-token", instance.Credentials.EncryptedAccessToken);
        Assert.Equal("Bearer", instance.Credentials.TokenType);
        Assert.Equal(2, instance.Credentials.Scopes.Count);
    }

    // ========================================================================
    // CredentialInfo Tests
    // ========================================================================

    [Fact]
    public void CredentialInfo_AllProperties()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var credentials = new CredentialInfo(
            "access-token",
            "refresh-token",
            expiresAt,
            ["read", "write"],
            "Bearer",
            new() { ["extra"] = "data" }
        );

        Assert.Equal("access-token", credentials.EncryptedAccessToken);
        Assert.Equal("refresh-token", credentials.EncryptedRefreshToken);
        Assert.Equal(expiresAt, credentials.ExpiresAt);
        Assert.Equal(2, credentials.Scopes.Count);
        Assert.Equal("Bearer", credentials.TokenType);
        Assert.NotNull(credentials.AdditionalData);
    }

    [Fact]
    public void CredentialInfo_MinimalProperties()
    {
        var credentials = new CredentialInfo(
            "token",
            null,
            null,
            [],
            null,
            null
        );

        Assert.Equal("token", credentials.EncryptedAccessToken);
        Assert.Null(credentials.EncryptedRefreshToken);
        Assert.Null(credentials.ExpiresAt);
        Assert.Empty(credentials.Scopes);
    }

    // ========================================================================
    // Webhook Tests
    // ========================================================================

    [Fact]
    public void WebhookEndpoint_AllProperties()
    {
        var webhook = new WebhookEndpoint(
            WebhookId.New(),
            IntegrationInstanceId.New(),
            "https://api.controlroom.io/webhooks/abc123",
            "secret-key",
            [WebhookEventType.Push, WebhookEventType.PullRequest],
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            100,
            95,
            5
        );

        Assert.Contains("webhooks", webhook.Url);
        Assert.NotEmpty(webhook.Secret);
        Assert.True(webhook.IsActive);
        Assert.Equal(2, webhook.SubscribedEvents.Count);
        Assert.Equal(100, webhook.TotalReceived);
        Assert.Equal(95, webhook.TotalProcessed);
        Assert.Equal(5, webhook.TotalFailed);
    }

    [Fact]
    public void WebhookEvent_AllProperties()
    {
        var evt = new WebhookEvent(
            Guid.NewGuid(),
            WebhookId.New(),
            IntegrationInstanceId.New(),
            WebhookEventType.PullRequestMerged,
            """{"action": "closed", "merged": true}""",
            new() { ["X-GitHub-Event"] = "pull_request" },
            DateTimeOffset.UtcNow,
            null,
            false,
            null
        );

        Assert.Equal(WebhookEventType.PullRequestMerged, evt.EventType);
        Assert.Contains("merged", evt.RawPayload);
        Assert.False(evt.IsProcessed);
        Assert.Null(evt.ProcessedAt);
    }

    [Fact]
    public void WebhookEvent_Processed()
    {
        var processedAt = DateTimeOffset.UtcNow;
        var evt = new WebhookEvent(
            Guid.NewGuid(),
            WebhookId.New(),
            IntegrationInstanceId.New(),
            WebhookEventType.Push,
            "{}",
            new(),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            processedAt,
            true,
            null
        );

        Assert.True(evt.IsProcessed);
        Assert.Equal(processedAt, evt.ProcessedAt);
    }

    // ========================================================================
    // API Key Tests
    // ========================================================================

    [Fact]
    public void ApiKey_AllProperties()
    {
        var apiKey = new ApiKey(
            ApiKeyId.New(),
            UserId.New(),
            TeamId.New(),
            "Production API Key",
            "cr_live_",
            "hashed-key-value",
            [ApiKeyScope.Read, ApiKeyScope.Write, ApiKeyScope.Admin],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1),
            DateTimeOffset.UtcNow,
            true,
            "192.168.1.0/24",
            42
        );

        Assert.Equal("Production API Key", apiKey.Name);
        Assert.Equal("cr_live_", apiKey.KeyPrefix);
        Assert.NotEmpty(apiKey.HashedKey);
        Assert.Equal(3, apiKey.Scopes.Count);
        Assert.True(apiKey.IsActive);
        Assert.Equal(42, apiKey.UsageCount);
    }

    [Fact]
    public void ApiKey_NoExpiration()
    {
        var apiKey = new ApiKey(
            ApiKeyId.New(),
            UserId.New(),
            null,
            "Test Key",
            "cr_test_",
            "hash",
            [ApiKeyScope.Read],
            DateTimeOffset.UtcNow,
            null,
            null,
            true,
            null,
            0
        );

        Assert.Null(apiKey.ExpiresAt);
        Assert.Null(apiKey.TeamId);
        Assert.Null(apiKey.AllowedIps);
    }

    // ========================================================================
    // Sync Job Tests
    // ========================================================================

    [Fact]
    public void SyncJob_AllProperties()
    {
        var job = CreateTestSyncJob();

        Assert.Equal(SyncDirection.Inbound, job.Direction);
        Assert.Equal("repositories", job.ResourceType);
        Assert.Equal(SyncStatus.Running, job.Status);
        Assert.Equal(100, job.TotalRecords);
        Assert.Equal(50, job.ProcessedRecords);
        Assert.Equal(2, job.FailedRecords);
    }

    [Fact]
    public void SyncJob_Completed()
    {
        var completedAt = DateTimeOffset.UtcNow;
        var job = new SyncJob(
            SyncJobId.New(),
            IntegrationInstanceId.New(),
            SyncDirection.Bidirectional,
            "issues",
            SyncStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            completedAt,
            500,
            495,
            5,
            null,
            new() { ["sync_type"] = "full" }
        );

        Assert.Equal(SyncStatus.Completed, job.Status);
        Assert.Equal(completedAt, job.CompletedAt);
        Assert.Null(job.Error);
    }

    [Fact]
    public void SyncJob_Failed()
    {
        var job = new SyncJob(
            SyncJobId.New(),
            IntegrationInstanceId.New(),
            SyncDirection.Outbound,
            "users",
            SyncStatus.Failed,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            1000,
            750,
            250,
            "Rate limit exceeded",
            null
        );

        Assert.Equal(SyncStatus.Failed, job.Status);
        Assert.Equal("Rate limit exceeded", job.Error);
    }

    // ========================================================================
    // Integration Event Tests
    // ========================================================================

    [Fact]
    public void IntegrationEvent_AllProperties()
    {
        var userId = UserId.New();
        var evt = new IntegrationEvent(
            Guid.NewGuid(),
            IntegrationInstanceId.New(),
            IntegrationEventType.Connected,
            "Integration connected successfully",
            userId,
            DateTimeOffset.UtcNow,
            new() { ["ip_address"] = "192.168.1.1" }
        );

        Assert.Equal(IntegrationEventType.Connected, evt.EventType);
        Assert.Equal("Integration connected successfully", evt.Description);
        Assert.Equal(userId, evt.TriggeredBy);
        Assert.NotNull(evt.Data);
    }

    [Fact]
    public void IntegrationEvent_SystemTriggered()
    {
        var evt = new IntegrationEvent(
            Guid.NewGuid(),
            IntegrationInstanceId.New(),
            IntegrationEventType.HealthCheckFailed,
            "Health check failed: timeout",
            null,
            DateTimeOffset.UtcNow,
            null
        );

        Assert.Null(evt.TriggeredBy);
        Assert.Equal(IntegrationEventType.HealthCheckFailed, evt.EventType);
    }

    // ========================================================================
    // Integration Health Check Tests
    // ========================================================================

    [Fact]
    public void IntegrationHealthCheck_Healthy()
    {
        var check = new IntegrationHealthCheck(
            IntegrationInstanceId.New(),
            IntegrationHealth.Healthy,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(150),
            "All systems operational",
            new() { ["api_version"] = "v3" }
        );

        Assert.Equal(IntegrationHealth.Healthy, check.Health);
        Assert.Equal(150, check.ResponseTime.TotalMilliseconds);
        Assert.NotNull(check.Details);
    }

    [Fact]
    public void IntegrationHealthCheck_Degraded()
    {
        var check = new IntegrationHealthCheck(
            IntegrationInstanceId.New(),
            IntegrationHealth.Degraded,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            "Response time degraded",
            null
        );

        Assert.Equal(IntegrationHealth.Degraded, check.Health);
        Assert.True(check.ResponseTime > TimeSpan.FromSeconds(1));
    }

    // ========================================================================
    // Integration Action Tests
    // ========================================================================

    [Fact]
    public void IntegrationAction_AllProperties()
    {
        var action = new IntegrationAction(
            "create_issue",
            "Create Issue",
            "Creates a new issue in the repository",
            [
                new ActionParameter("title", "Title", "Issue title", ConfigFieldType.String, true, null),
                new ActionParameter("body", "Body", "Issue body", ConfigFieldType.String, false, ""),
                new ActionParameter("labels", "Labels", "Issue labels", ConfigFieldType.MultiSelect, false, null)
            ],
            "Issue"
        );

        Assert.Equal("create_issue", action.Name);
        Assert.Equal("Create Issue", action.DisplayName);
        Assert.Equal(3, action.Parameters.Count);
        Assert.Equal("Issue", action.ReturnType);
    }

    [Fact]
    public void ActionParameter_Required()
    {
        var param = new ActionParameter(
            "api_key",
            "API Key",
            "Your API key",
            ConfigFieldType.Secret,
            true,
            null
        );

        Assert.True(param.IsRequired);
        Assert.Null(param.DefaultValue);
        Assert.Equal(ConfigFieldType.Secret, param.Type);
    }

    [Fact]
    public void ActionParameter_Optional()
    {
        var param = new ActionParameter(
            "timeout",
            "Timeout",
            "Request timeout in seconds",
            ConfigFieldType.Number,
            false,
            "30"
        );

        Assert.False(param.IsRequired);
        Assert.Equal("30", param.DefaultValue);
    }

    // ========================================================================
    // Action Result Tests
    // ========================================================================

    [Fact]
    public void ActionResult_Success()
    {
        var result = new ActionResult(
            true,
            "Issue created successfully",
            new { IssueId = 123, Url = "https://github.com/issue/123" },
            new() { ["request_id"] = "abc123" },
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(250)
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(250, result.Duration.TotalMilliseconds);
    }

    [Fact]
    public void ActionResult_Failure()
    {
        var result = new ActionResult(
            false,
            "Authentication failed: invalid token",
            null,
            null,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(50)
        );

        Assert.False(result.Success);
        Assert.Contains("Authentication failed", result.Message);
        Assert.Null(result.Data);
    }

    // ========================================================================
    // IntegrationConfig Tests
    // ========================================================================

    [Fact]
    public void IntegrationConfig_OAuth()
    {
        var config = new IntegrationConfig(
            [],
            ["repo", "read:org", "read:user"],
            "https://github.com/login/oauth/authorize",
            "https://github.com/login/oauth/access_token",
            "https://api.controlroom.io/oauth/callback",
            "https://api.github.com",
            new() { ["Accept"] = "application/vnd.github+json" },
            5000,
            30000
        );

        Assert.NotNull(config.OAuthAuthorizationUrl);
        Assert.NotNull(config.OAuthTokenUrl);
        Assert.Equal(3, config.RequiredScopes.Count);
        Assert.Equal(5000, config.RateLimitPerMinute);
    }

    [Fact]
    public void IntegrationConfig_ApiKey()
    {
        var config = new IntegrationConfig(
            [
                new ConfigField("api_key", "API Key", "Your API key", ConfigFieldType.Secret, true, true, null, null, null),
                new ConfigField("region", "Region", "API region", ConfigFieldType.Select, false, false, "us", null, ["us", "eu", "ap"])
            ],
            [],
            null,
            null,
            null,
            "https://api.service.com",
            new(),
            1000,
            30000
        );

        Assert.Null(config.OAuthAuthorizationUrl);
        Assert.Equal(2, config.Fields.Count);
        Assert.Equal(3, config.Fields[1].AllowedValues?.Count);
    }

    // ========================================================================
    // ConfigField Tests
    // ========================================================================

    [Fact]
    public void ConfigField_Secret()
    {
        var field = new ConfigField(
            "api_secret",
            "API Secret",
            "Your API secret key",
            ConfigFieldType.Secret,
            true,
            true,
            null,
            @"^[a-zA-Z0-9]{32,64}$",
            null
        );

        Assert.True(field.IsRequired);
        Assert.True(field.IsSecret);
        Assert.NotNull(field.ValidationPattern);
    }

    [Fact]
    public void ConfigField_Select()
    {
        var field = new ConfigField(
            "environment",
            "Environment",
            "Target environment",
            ConfigFieldType.Select,
            true,
            false,
            "production",
            null,
            ["development", "staging", "production"]
        );

        Assert.Equal(ConfigFieldType.Select, field.Type);
        Assert.Equal(3, field.AllowedValues?.Count);
        Assert.Equal("production", field.DefaultValue);
    }

    // ========================================================================
    // IntegrationCapabilities Tests
    // ========================================================================

    [Fact]
    public void IntegrationCapabilities_Full()
    {
        var capabilities = new IntegrationCapabilities(
            SupportsWebhooks: true,
            SupportsPush: true,
            SupportsPull: true,
            SupportsSync: true,
            SupportsEvents: true,
            SupportsActions: true,
            SupportsHealthCheck: true,
            SupportedActions: ["create", "read", "update", "delete"],
            SupportedEvents: [WebhookEventType.Push, WebhookEventType.PullRequest]
        );

        Assert.True(capabilities.SupportsWebhooks);
        Assert.True(capabilities.SupportsPush);
        Assert.True(capabilities.SupportsPull);
        Assert.Equal(4, capabilities.SupportedActions.Count);
        Assert.Equal(2, capabilities.SupportedEvents.Count);
    }

    [Fact]
    public void IntegrationCapabilities_Limited()
    {
        var capabilities = new IntegrationCapabilities(
            SupportsWebhooks: false,
            SupportsPush: false,
            SupportsPull: true,
            SupportsSync: true,
            SupportsEvents: false,
            SupportsActions: true,
            SupportsHealthCheck: true,
            SupportedActions: ["query"],
            SupportedEvents: []
        );

        Assert.False(capabilities.SupportsWebhooks);
        Assert.False(capabilities.SupportsPush);
        Assert.True(capabilities.SupportsPull);
        Assert.Empty(capabilities.SupportedEvents);
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private static IntegrationInstance CreateTestInstance()
    {
        return new IntegrationInstance(
            IntegrationInstanceId.New(),
            IntegrationId.New(),
            UserId.New(),
            null,
            "my-github",
            "My GitHub Account",
            IntegrationStatus.Connected,
            IntegrationHealth.Healthy,
            new() { ["org"] = "my-org" },
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null
        );
    }

    private static SyncJob CreateTestSyncJob()
    {
        return new SyncJob(
            SyncJobId.New(),
            IntegrationInstanceId.New(),
            SyncDirection.Inbound,
            "repositories",
            SyncStatus.Running,
            DateTimeOffset.UtcNow,
            null,
            100,
            50,
            2,
            null,
            null
        );
    }
}
