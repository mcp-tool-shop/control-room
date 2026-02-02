using ControlRoom.Domain.Model;
using IntegrationModel = ControlRoom.Domain.Model.Integration;

namespace ControlRoom.Tests.Unit.Domain;

/// <summary>
/// Unit tests for Integration domain model.
/// </summary>
public sealed class IntegrationTests
{
    // ========================================================================
    // ID Tests
    // ========================================================================

    [Fact]
    public void IntegrationId_New_GeneratesUniqueIds()
    {
        var id1 = IntegrationId.New();
        var id2 = IntegrationId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void IntegrationId_Parse_RoundTrips()
    {
        var original = IntegrationId.New();
        var parsed = IntegrationId.Parse(original.ToString());

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void IntegrationInstanceId_New_GeneratesUniqueIds()
    {
        var id1 = IntegrationInstanceId.New();
        var id2 = IntegrationInstanceId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void WebhookId_New_GeneratesUniqueIds()
    {
        var id1 = WebhookId.New();
        var id2 = WebhookId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ApiKeyId_New_GeneratesUniqueIds()
    {
        var id1 = ApiKeyId.New();
        var id2 = ApiKeyId.New();

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void SyncJobId_New_GeneratesUniqueIds()
    {
        var id1 = SyncJobId.New();
        var id2 = SyncJobId.New();

        Assert.NotEqual(id1, id2);
    }

    // ========================================================================
    // Integration Record Tests
    // ========================================================================

    [Fact]
    public void Integration_AllProperties()
    {
        var id = IntegrationId.New();
        var capabilities = new IntegrationCapabilities(
            SupportsWebhooks: true,
            SupportsPush: true,
            SupportsPull: true,
            SupportsSync: true,
            SupportsEvents: true,
            SupportsActions: true,
            SupportsHealthCheck: true,
            SupportedActions: ["create_issue", "update_issue"],
            SupportedEvents: [WebhookEventType.IssueCreated, WebhookEventType.IssueUpdated]
        );
        var config = new IntegrationConfig(
            Fields: [],
            RequiredScopes: ["read", "write"],
            OAuthAuthorizationUrl: "https://auth.example.com",
            OAuthTokenUrl: "https://token.example.com",
            OAuthRedirectUri: null,
            ApiBaseUrl: "https://api.example.com",
            DefaultHeaders: new() { ["Accept"] = "application/json" },
            RateLimitPerMinute: 100,
            TimeoutMs: 30000
        );

        var integration = new IntegrationModel(
            id,
            "github",
            "GitHub",
            "GitHub source control integration",
            IntegrationCategory.SourceControl,
            AuthMethod.OAuth2,
            "https://github.com/icon.png",
            "https://docs.github.com",
            true,
            true,
            capabilities,
            config,
            DateTimeOffset.UtcNow,
            null
        );

        Assert.Equal(id, integration.Id);
        Assert.Equal("github", integration.Name);
        Assert.Equal("GitHub", integration.DisplayName);
        Assert.Equal(IntegrationCategory.SourceControl, integration.Category);
        Assert.Equal(AuthMethod.OAuth2, integration.AuthMethod);
        Assert.True(integration.IsBuiltIn);
        Assert.True(integration.IsEnabled);
        Assert.True(integration.Capabilities.SupportsWebhooks);
        Assert.Equal(2, integration.Capabilities.SupportedActions.Count);
    }

    [Fact]
    public void IntegrationInstance_AllProperties()
    {
        var id = IntegrationInstanceId.New();
        var integrationId = IntegrationId.New();
        var ownerId = UserId.New();
        var teamId = TeamId.New();

        var instance = new IntegrationInstance(
            id,
            integrationId,
            ownerId,
            teamId,
            "my-github",
            "My GitHub",
            IntegrationStatus.Connected,
            IntegrationHealth.Healthy,
            new() { ["repo"] = "my-repo" },
            new CredentialInfo(
                "encrypted-token",
                "encrypted-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                ["repo", "user"],
                "Bearer",
                null
            ),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null
        );

        Assert.Equal(id, instance.Id);
        Assert.Equal(integrationId, instance.IntegrationId);
        Assert.Equal(ownerId, instance.OwnerId);
        Assert.Equal(teamId, instance.TeamId);
        Assert.Equal(IntegrationStatus.Connected, instance.Status);
        Assert.Equal(IntegrationHealth.Healthy, instance.Health);
        Assert.NotNull(instance.Credentials);
        Assert.Equal("Bearer", instance.Credentials.TokenType);
    }

    // ========================================================================
    // Webhook Tests
    // ========================================================================

    [Fact]
    public void WebhookEndpoint_AllProperties()
    {
        var id = WebhookId.New();
        var instanceId = IntegrationInstanceId.New();

        var webhook = new WebhookEndpoint(
            id,
            instanceId,
            "https://api.controlroom.io/webhooks/abc123",
            "secret-key-123",
            [WebhookEventType.Push, WebhookEventType.PullRequest],
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            100,
            95,
            5
        );

        Assert.Equal(id, webhook.Id);
        Assert.Equal(instanceId, webhook.InstanceId);
        Assert.True(webhook.IsActive);
        Assert.Equal(2, webhook.SubscribedEvents.Count);
        Assert.Equal(100, webhook.TotalReceived);
        Assert.Equal(95, webhook.TotalProcessed);
        Assert.Equal(5, webhook.TotalFailed);
    }

    [Fact]
    public void WebhookEvent_AllProperties()
    {
        var id = Guid.NewGuid();
        var webhookId = WebhookId.New();
        var instanceId = IntegrationInstanceId.New();

        var evt = new WebhookEvent(
            id,
            webhookId,
            instanceId,
            WebhookEventType.Push,
            """{"ref": "refs/heads/main"}""",
            new() { ["X-Hub-Signature"] = "sha256=abc" },
            DateTimeOffset.UtcNow,
            null,
            false,
            null
        );

        Assert.Equal(id, evt.Id);
        Assert.Equal(WebhookEventType.Push, evt.EventType);
        Assert.False(evt.IsProcessed);
        Assert.Contains("refs/heads/main", evt.RawPayload);
    }

    // ========================================================================
    // API Key Tests
    // ========================================================================

    [Fact]
    public void ApiKey_AllProperties()
    {
        var id = ApiKeyId.New();
        var ownerId = UserId.New();

        var apiKey = new ApiKey(
            id,
            ownerId,
            null,
            "Production API Key",
            "cr_live",
            "hashed-key-value",
            [ApiKeyScope.Read, ApiKeyScope.Write],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1),
            DateTimeOffset.UtcNow,
            true,
            "192.168.1.0/24",
            42
        );

        Assert.Equal(id, apiKey.Id);
        Assert.Equal(ownerId, apiKey.OwnerId);
        Assert.Equal("cr_live", apiKey.KeyPrefix);
        Assert.True(apiKey.IsActive);
        Assert.Equal(2, apiKey.Scopes.Count);
        Assert.Equal(42, apiKey.UsageCount);
    }

    // ========================================================================
    // Sync Job Tests
    // ========================================================================

    [Fact]
    public void SyncJob_AllProperties()
    {
        var id = SyncJobId.New();
        var instanceId = IntegrationInstanceId.New();

        var job = new SyncJob(
            id,
            instanceId,
            SyncDirection.Inbound,
            "issues",
            SyncStatus.Running,
            DateTimeOffset.UtcNow,
            null,
            1000,
            500,
            10,
            null,
            null
        );

        Assert.Equal(id, job.Id);
        Assert.Equal(SyncDirection.Inbound, job.Direction);
        Assert.Equal(SyncStatus.Running, job.Status);
        Assert.Equal(1000, job.TotalRecords);
        Assert.Equal(500, job.ProcessedRecords);
        Assert.Equal(10, job.FailedRecords);
    }

    // ========================================================================
    // Extension Method Tests
    // ========================================================================

    [Theory]
    [InlineData(IntegrationStatus.Connected, true)]
    [InlineData(IntegrationStatus.Pending, false)]
    [InlineData(IntegrationStatus.Disconnected, false)]
    [InlineData(IntegrationStatus.Error, false)]
    public void IsConnected_ReturnsCorrectValue(IntegrationStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsConnected());
    }

    [Theory]
    [InlineData(IntegrationStatus.Expired, true)]
    [InlineData(IntegrationStatus.Revoked, true)]
    [InlineData(IntegrationStatus.Error, true)]
    [InlineData(IntegrationStatus.Connected, false)]
    [InlineData(IntegrationStatus.Pending, false)]
    public void NeedsReconnection_ReturnsCorrectValue(IntegrationStatus status, bool expected)
    {
        Assert.Equal(expected, status.NeedsReconnection());
    }

    [Theory]
    [InlineData(IntegrationHealth.Healthy, true)]
    [InlineData(IntegrationHealth.Unknown, true)]
    [InlineData(IntegrationHealth.Degraded, false)]
    [InlineData(IntegrationHealth.Unhealthy, false)]
    [InlineData(IntegrationHealth.Unreachable, false)]
    public void IsHealthy_ReturnsCorrectValue(IntegrationHealth health, bool expected)
    {
        Assert.Equal(expected, health.IsHealthy());
    }

    [Theory]
    [InlineData(AuthMethod.OAuth2, true)]
    [InlineData(AuthMethod.OAuth2PKCE, true)]
    [InlineData(AuthMethod.ApiKey, false)]
    [InlineData(AuthMethod.BearerToken, false)]
    [InlineData(AuthMethod.None, false)]
    public void RequiresOAuth_ReturnsCorrectValue(AuthMethod method, bool expected)
    {
        Assert.Equal(expected, method.RequiresOAuth());
    }

    [Theory]
    [InlineData(AuthMethod.None, false)]
    [InlineData(AuthMethod.ApiKey, true)]
    [InlineData(AuthMethod.OAuth2, true)]
    [InlineData(AuthMethod.BasicAuth, true)]
    public void RequiresCredentials_ReturnsCorrectValue(AuthMethod method, bool expected)
    {
        Assert.Equal(expected, method.RequiresCredentials());
    }

    // ========================================================================
    // Icon Tests
    // ========================================================================

    [Theory]
    [InlineData(IntegrationCategory.CloudProvider, "\u2601\uFE0F")]
    [InlineData(IntegrationCategory.SourceControl, "\U0001F4BB")]
    [InlineData(IntegrationCategory.IssueTracking, "\U0001F4CB")]
    [InlineData(IntegrationCategory.Monitoring, "\U0001F4CA")]
    [InlineData(IntegrationCategory.Alerting, "\U0001F6A8")]
    [InlineData(IntegrationCategory.Communication, "\U0001F4AC")]
    [InlineData(IntegrationCategory.CI_CD, "\u2699\uFE0F")]
    [InlineData(IntegrationCategory.Database, "\U0001F5C4\uFE0F")]
    [InlineData(IntegrationCategory.Storage, "\U0001F4E6")]
    [InlineData(IntegrationCategory.Identity, "\U0001F511")]
    [InlineData(IntegrationCategory.Custom, "\U0001F527")]
    public void GetCategoryIcon_ReturnsCorrectIcon(IntegrationCategory category, string expected)
    {
        Assert.Equal(expected, category.GetCategoryIcon());
    }

    [Theory]
    [InlineData(IntegrationStatus.Connected, "\u2705")]
    [InlineData(IntegrationStatus.Connecting, "\U0001F504")]
    [InlineData(IntegrationStatus.Pending, "\u23F3")]
    [InlineData(IntegrationStatus.Disconnected, "\u26AA")]
    [InlineData(IntegrationStatus.Error, "\u274C")]
    [InlineData(IntegrationStatus.Expired, "\u23F0")]
    [InlineData(IntegrationStatus.Revoked, "\U0001F6AB")]
    [InlineData(IntegrationStatus.Suspended, "\u23F8\uFE0F")]
    public void GetStatusIcon_ReturnsCorrectIcon(IntegrationStatus status, string expected)
    {
        Assert.Equal(expected, status.GetStatusIcon());
    }

    [Theory]
    [InlineData(IntegrationHealth.Healthy, "\U0001F49A")]
    [InlineData(IntegrationHealth.Degraded, "\U0001F7E1")]
    [InlineData(IntegrationHealth.Unhealthy, "\U0001F534")]
    [InlineData(IntegrationHealth.Unreachable, "\u26AB")]
    [InlineData(IntegrationHealth.Unknown, "\u2754")]
    public void GetHealthIcon_ReturnsCorrectIcon(IntegrationHealth health, string expected)
    {
        Assert.Equal(expected, health.GetHealthIcon());
    }

    [Theory]
    [InlineData(WebhookEventType.Push, "\u2B06\uFE0F")]
    [InlineData(WebhookEventType.PullRequest, "\U0001F500")]
    [InlineData(WebhookEventType.PullRequestMerged, "\U0001F91D")]
    [InlineData(WebhookEventType.Release, "\U0001F680")]
    [InlineData(WebhookEventType.BuildStarted, "\U0001F3D7\uFE0F")]
    [InlineData(WebhookEventType.BuildCompleted, "\U0001F3C1")]
    [InlineData(WebhookEventType.BuildFailed, "\U0001F4A5")]
    [InlineData(WebhookEventType.DeploymentStarted, "\U0001F6EB")]
    [InlineData(WebhookEventType.DeploymentCompleted, "\U0001F6EC")]
    [InlineData(WebhookEventType.AlertTriggered, "\U0001F6A8")]
    [InlineData(WebhookEventType.AlertResolved, "\U0001F49A")]
    public void GetEventIcon_ReturnsCorrectIcon(WebhookEventType eventType, string expected)
    {
        Assert.Equal(expected, eventType.GetEventIcon());
    }

    // ========================================================================
    // Config Field Tests
    // ========================================================================

    [Fact]
    public void ConfigField_AllProperties()
    {
        var field = new ConfigField(
            "api_key",
            "API Key",
            "Your API key for authentication",
            ConfigFieldType.Secret,
            true,
            true,
            null,
            @"^[a-zA-Z0-9]{32}$",
            null
        );

        Assert.Equal("api_key", field.Name);
        Assert.Equal("API Key", field.DisplayName);
        Assert.Equal(ConfigFieldType.Secret, field.Type);
        Assert.True(field.IsRequired);
        Assert.True(field.IsSecret);
    }

    [Fact]
    public void ConfigField_WithAllowedValues()
    {
        var field = new ConfigField(
            "region",
            "Region",
            "Cloud region",
            ConfigFieldType.Select,
            true,
            false,
            "us-east-1",
            null,
            ["us-east-1", "us-west-2", "eu-west-1"]
        );

        Assert.Equal(ConfigFieldType.Select, field.Type);
        Assert.Equal(3, field.AllowedValues?.Count);
        Assert.Equal("us-east-1", field.DefaultValue);
    }

    // ========================================================================
    // Action Tests
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
                new ActionParameter("body", "Body", "Issue body", ConfigFieldType.String, false, "")
            ],
            "Issue"
        );

        Assert.Equal("create_issue", action.Name);
        Assert.Equal("Create Issue", action.DisplayName);
        Assert.Equal(2, action.Parameters.Count);
        Assert.Equal("Issue", action.ReturnType);
    }

    [Fact]
    public void ActionResult_Success()
    {
        var result = new ActionResult(
            true,
            "Issue created successfully",
            new { IssueId = 123 },
            new() { ["url"] = "https://github.com/issue/123" },
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(250)
        );

        Assert.True(result.Success);
        Assert.Equal("Issue created successfully", result.Message);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void ActionResult_Failure()
    {
        var result = new ActionResult(
            false,
            "Authentication failed",
            null,
            null,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(50)
        );

        Assert.False(result.Success);
        Assert.Equal("Authentication failed", result.Message);
        Assert.Null(result.Data);
    }

    // ========================================================================
    // Health Check Tests
    // ========================================================================

    [Fact]
    public void IntegrationHealthCheck_AllProperties()
    {
        var instanceId = IntegrationInstanceId.New();

        var check = new IntegrationHealthCheck(
            instanceId,
            IntegrationHealth.Healthy,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(150),
            "All systems operational",
            new() { ["api_version"] = "v3" }
        );

        Assert.Equal(instanceId, check.InstanceId);
        Assert.Equal(IntegrationHealth.Healthy, check.Health);
        Assert.Equal(150, check.ResponseTime.TotalMilliseconds);
    }

    // ========================================================================
    // Event Tests
    // ========================================================================

    [Fact]
    public void IntegrationEvent_AllProperties()
    {
        var id = Guid.NewGuid();
        var instanceId = IntegrationInstanceId.New();
        var userId = UserId.New();

        var evt = new IntegrationEvent(
            id,
            instanceId,
            IntegrationEventType.Connected,
            "Integration connected successfully",
            userId,
            DateTimeOffset.UtcNow,
            new() { ["ip_address"] = "192.168.1.1" }
        );

        Assert.Equal(id, evt.Id);
        Assert.Equal(instanceId, evt.InstanceId);
        Assert.Equal(IntegrationEventType.Connected, evt.EventType);
        Assert.Equal(userId, evt.TriggeredBy);
    }

    [Theory]
    [InlineData(IntegrationEventType.Connected)]
    [InlineData(IntegrationEventType.Disconnected)]
    [InlineData(IntegrationEventType.Refreshed)]
    [InlineData(IntegrationEventType.Synced)]
    [InlineData(IntegrationEventType.ActionExecuted)]
    [InlineData(IntegrationEventType.WebhookReceived)]
    [InlineData(IntegrationEventType.HealthCheckPassed)]
    [InlineData(IntegrationEventType.HealthCheckFailed)]
    [InlineData(IntegrationEventType.Error)]
    [InlineData(IntegrationEventType.ConfigUpdated)]
    [InlineData(IntegrationEventType.CredentialsRotated)]
    public void IntegrationEventType_AllValues(IntegrationEventType eventType)
    {
        var evt = new IntegrationEvent(
            Guid.NewGuid(),
            IntegrationInstanceId.New(),
            eventType,
            "Test event",
            null,
            DateTimeOffset.UtcNow,
            null
        );

        Assert.Equal(eventType, evt.EventType);
    }
}
