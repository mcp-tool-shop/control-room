using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.UseCases;

/// <summary>
/// Use case for managing integrations and connected accounts.
/// </summary>
public sealed class IntegrationHub
{
    private readonly Db _db;
    private readonly IntegrationQueries _queries;
    private readonly TeamManagement _teamManagement;

    public IntegrationHub(Db db, TeamManagement teamManagement)
    {
        _db = db;
        _queries = new IntegrationQueries(db);
        _teamManagement = teamManagement;
    }

    // Events
    public event EventHandler<IntegrationConnectedEventArgs>? IntegrationConnected;
    public event EventHandler<IntegrationDisconnectedEventArgs>? IntegrationDisconnected;
    public event EventHandler<WebhookReceivedEventArgs>? WebhookReceived;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    // ========================================================================
    // Integration Catalog
    // ========================================================================

    /// <summary>
    /// Get all available integrations.
    /// </summary>
    public IReadOnlyList<Integration> GetAvailableIntegrations(IntegrationCategory? category = null)
    {
        return _queries.GetIntegrations(category, enabledOnly: true);
    }

    /// <summary>
    /// Get integration by ID.
    /// </summary>
    public Integration? GetIntegration(IntegrationId id)
    {
        return _queries.GetIntegration(id);
    }

    /// <summary>
    /// Get integration by name.
    /// </summary>
    public Integration? GetIntegrationByName(string name)
    {
        return _queries.GetIntegrationByName(name);
    }

    /// <summary>
    /// Register a new integration definition.
    /// </summary>
    public Integration RegisterIntegration(
        string name,
        string displayName,
        string description,
        IntegrationCategory category,
        AuthMethod authMethod,
        IntegrationCapabilities capabilities,
        IntegrationConfig config,
        string? iconUrl = null,
        string? documentationUrl = null)
    {
        var integration = new Integration(
            IntegrationId.New(),
            name,
            displayName,
            description,
            category,
            authMethod,
            iconUrl ?? "",
            documentationUrl ?? "",
            false, // Not built-in
            true,  // Enabled
            capabilities,
            config,
            DateTimeOffset.UtcNow,
            null
        );

        _queries.InsertIntegration(integration);
        return integration;
    }

    /// <summary>
    /// Seed built-in integrations.
    /// </summary>
    public void SeedBuiltInIntegrations()
    {
        var builtIns = GetBuiltInIntegrationDefinitions();
        foreach (var integration in builtIns)
        {
            var existing = _queries.GetIntegrationByName(integration.Name);
            if (existing is null)
            {
                _queries.InsertIntegration(integration);
            }
        }
    }

    // ========================================================================
    // Instance Management
    // ========================================================================

    /// <summary>
    /// Get all instances for current user.
    /// </summary>
    public IReadOnlyList<IntegrationInstance> GetMyInstances()
    {
        var user = _teamManagement.GetCurrentUser();
        return _queries.GetUserInstances(user.Id);
    }

    /// <summary>
    /// Get instances for a team.
    /// </summary>
    public IReadOnlyList<IntegrationInstance> GetTeamInstances(TeamId teamId)
    {
        return _queries.GetTeamInstances(teamId);
    }

    /// <summary>
    /// Get a specific instance.
    /// </summary>
    public IntegrationInstance? GetInstance(IntegrationInstanceId id)
    {
        return _queries.GetInstance(id);
    }

    /// <summary>
    /// Create a new integration instance (start connection process).
    /// </summary>
    public IntegrationInstance CreateInstance(
        IntegrationId integrationId,
        string name,
        string displayName,
        TeamId? teamId = null,
        Dictionary<string, string>? configuration = null)
    {
        var user = _teamManagement.GetCurrentUser();
        var integration = _queries.GetIntegration(integrationId)
            ?? throw new InvalidOperationException("Integration not found");

        var instance = new IntegrationInstance(
            IntegrationInstanceId.New(),
            integrationId,
            user.Id,
            teamId,
            name,
            displayName,
            IntegrationStatus.Pending,
            IntegrationHealth.Unknown,
            configuration ?? new(),
            null,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null,
            null
        );

        _queries.InsertInstance(instance);

        LogIntegrationEvent(instance.Id, IntegrationEventType.Connected, "Instance created", user.Id);

        return instance;
    }

    /// <summary>
    /// Connect an instance with credentials.
    /// </summary>
    public IntegrationInstance Connect(
        IntegrationInstanceId instanceId,
        CredentialInfo credentials)
    {
        var user = _teamManagement.GetCurrentUser();
        var instance = _queries.GetInstance(instanceId)
            ?? throw new InvalidOperationException("Instance not found");

        if (instance.OwnerId != user.Id)
            throw new UnauthorizedAccessException("Not authorized to modify this instance");

        // Update status and credentials
        _queries.UpdateInstanceStatus(instanceId, IntegrationStatus.Connecting);
        _queries.UpdateInstanceCredentials(instanceId, credentials);

        // Simulate connection (in real implementation, would validate with external API)
        _queries.UpdateInstanceStatus(instanceId, IntegrationStatus.Connected);
        _queries.UpdateInstanceHealth(instanceId, IntegrationHealth.Healthy);

        LogIntegrationEvent(instanceId, IntegrationEventType.Connected, "Integration connected", user.Id);

        var updated = _queries.GetInstance(instanceId)!;
        IntegrationConnected?.Invoke(this, new IntegrationConnectedEventArgs { Instance = updated });

        return updated;
    }

    /// <summary>
    /// Disconnect an instance.
    /// </summary>
    public void Disconnect(IntegrationInstanceId instanceId)
    {
        var user = _teamManagement.GetCurrentUser();
        var instance = _queries.GetInstance(instanceId)
            ?? throw new InvalidOperationException("Instance not found");

        if (instance.OwnerId != user.Id)
            throw new UnauthorizedAccessException("Not authorized to modify this instance");

        _queries.UpdateInstanceStatus(instanceId, IntegrationStatus.Disconnected);

        LogIntegrationEvent(instanceId, IntegrationEventType.Disconnected, "Integration disconnected", user.Id);

        IntegrationDisconnected?.Invoke(this, new IntegrationDisconnectedEventArgs { InstanceId = instanceId });
    }

    /// <summary>
    /// Delete an instance and all related data.
    /// </summary>
    public void DeleteInstance(IntegrationInstanceId instanceId)
    {
        var user = _teamManagement.GetCurrentUser();
        var instance = _queries.GetInstance(instanceId)
            ?? throw new InvalidOperationException("Instance not found");

        if (instance.OwnerId != user.Id)
            throw new UnauthorizedAccessException("Not authorized to delete this instance");

        _queries.DeleteInstance(instanceId);
    }

    /// <summary>
    /// Refresh credentials for an instance.
    /// </summary>
    public IntegrationInstance RefreshCredentials(IntegrationInstanceId instanceId, CredentialInfo newCredentials)
    {
        var user = _teamManagement.GetCurrentUser();
        var instance = _queries.GetInstance(instanceId)
            ?? throw new InvalidOperationException("Instance not found");

        if (instance.OwnerId != user.Id)
            throw new UnauthorizedAccessException("Not authorized to modify this instance");

        _queries.UpdateInstanceCredentials(instanceId, newCredentials);
        _queries.UpdateInstanceStatus(instanceId, IntegrationStatus.Connected);

        LogIntegrationEvent(instanceId, IntegrationEventType.CredentialsRotated, "Credentials refreshed", user.Id);

        return _queries.GetInstance(instanceId)!;
    }

    // ========================================================================
    // Health Checks
    // ========================================================================

    /// <summary>
    /// Check health of an integration instance.
    /// </summary>
    public IntegrationHealthCheck CheckHealth(IntegrationInstanceId instanceId)
    {
        var instance = _queries.GetInstance(instanceId)
            ?? throw new InvalidOperationException("Instance not found");

        var startTime = DateTimeOffset.UtcNow;

        // Simulate health check (in real implementation, would call external API)
        var health = instance.Status == IntegrationStatus.Connected
            ? IntegrationHealth.Healthy
            : IntegrationHealth.Unreachable;

        var responseTime = DateTimeOffset.UtcNow - startTime;

        _queries.UpdateInstanceHealth(instanceId, health);

        var eventType = health == IntegrationHealth.Healthy
            ? IntegrationEventType.HealthCheckPassed
            : IntegrationEventType.HealthCheckFailed;

        LogIntegrationEvent(instanceId, eventType, $"Health check: {health}", null);

        return new IntegrationHealthCheck(
            instanceId,
            health,
            DateTimeOffset.UtcNow,
            responseTime,
            health == IntegrationHealth.Healthy ? "All systems operational" : "Connection failed",
            null
        );
    }

    /// <summary>
    /// Check health of all connected instances.
    /// </summary>
    public IReadOnlyList<IntegrationHealthCheck> CheckAllHealth()
    {
        var user = _teamManagement.GetCurrentUser();
        var instances = _queries.GetUserInstances(user.Id)
            .Where(i => i.Status == IntegrationStatus.Connected)
            .ToList();

        return instances.Select(i => CheckHealth(i.Id)).ToList();
    }

    // ========================================================================
    // Webhooks
    // ========================================================================

    /// <summary>
    /// Create a webhook endpoint for an instance.
    /// </summary>
    public WebhookEndpoint CreateWebhook(
        IntegrationInstanceId instanceId,
        IReadOnlyList<WebhookEventType> subscribedEvents)
    {
        var user = _teamManagement.GetCurrentUser();
        var instance = _queries.GetInstance(instanceId)
            ?? throw new InvalidOperationException("Instance not found");

        if (instance.OwnerId != user.Id)
            throw new UnauthorizedAccessException("Not authorized to modify this instance");

        var webhookId = WebhookId.New();
        var secret = GenerateWebhookSecret();
        var url = $"https://api.controlroom.io/webhooks/{webhookId}";

        var webhook = new WebhookEndpoint(
            webhookId,
            instanceId,
            url,
            secret,
            subscribedEvents,
            true,
            DateTimeOffset.UtcNow,
            null,
            0,
            0,
            0
        );

        _queries.InsertWebhook(webhook);

        return webhook;
    }

    /// <summary>
    /// Get webhooks for an instance.
    /// </summary>
    public IReadOnlyList<WebhookEndpoint> GetWebhooks(IntegrationInstanceId instanceId)
    {
        return _queries.GetInstanceWebhooks(instanceId);
    }

    /// <summary>
    /// Process incoming webhook event.
    /// </summary>
    public void ProcessWebhook(
        WebhookId webhookId,
        WebhookEventType eventType,
        string payload,
        Dictionary<string, string> headers)
    {
        var webhook = _queries.GetWebhook(webhookId)
            ?? throw new InvalidOperationException("Webhook not found");

        var evt = new WebhookEvent(
            Guid.NewGuid(),
            webhookId,
            webhook.InstanceId,
            eventType,
            payload,
            headers,
            DateTimeOffset.UtcNow,
            null,
            false,
            null
        );

        _queries.InsertWebhookEvent(evt);

        // Process the event
        try
        {
            // In real implementation, would route to appropriate handler
            _queries.MarkEventProcessed(evt.Id);
            _queries.IncrementWebhookCounters(webhookId, true);

            LogIntegrationEvent(webhook.InstanceId, IntegrationEventType.WebhookReceived,
                $"Webhook received: {eventType}", null, new() { ["event_type"] = eventType.ToString() });

            WebhookReceived?.Invoke(this, new WebhookReceivedEventArgs
            {
                WebhookId = webhookId,
                EventType = eventType,
                Payload = payload
            });
        }
        catch (Exception ex)
        {
            _queries.MarkEventProcessed(evt.Id, ex.Message);
            _queries.IncrementWebhookCounters(webhookId, false);
            throw;
        }
    }

    // ========================================================================
    // API Keys
    // ========================================================================

    /// <summary>
    /// Create a new API key.
    /// </summary>
    public (ApiKey Key, string RawKey) CreateApiKey(
        string name,
        IReadOnlyList<ApiKeyScope> scopes,
        TeamId? teamId = null,
        DateTimeOffset? expiresAt = null,
        string? allowedIps = null)
    {
        var user = _teamManagement.GetCurrentUser();

        // Generate the raw key
        var rawKey = GenerateApiKey();
        var prefix = rawKey[..8];
        var hashedKey = HashApiKey(rawKey);

        var apiKey = new ApiKey(
            ApiKeyId.New(),
            user.Id,
            teamId,
            name,
            prefix,
            hashedKey,
            scopes,
            DateTimeOffset.UtcNow,
            expiresAt,
            null,
            true,
            allowedIps,
            0
        );

        _queries.InsertApiKey(apiKey);

        return (apiKey, rawKey);
    }

    /// <summary>
    /// Get API keys for current user.
    /// </summary>
    public IReadOnlyList<ApiKey> GetMyApiKeys()
    {
        var user = _teamManagement.GetCurrentUser();
        return _queries.GetUserApiKeys(user.Id);
    }

    /// <summary>
    /// Validate an API key.
    /// </summary>
    public ApiKey? ValidateApiKey(string rawKey)
    {
        if (rawKey.Length < 8)
            return null;

        var prefix = rawKey[..8];
        var apiKey = _queries.GetApiKeyByPrefix(prefix);

        if (apiKey is null)
            return null;

        // Verify hash
        var hashedKey = HashApiKey(rawKey);
        if (hashedKey != apiKey.HashedKey)
            return null;

        // Check expiration
        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTimeOffset.UtcNow)
            return null;

        // Update usage
        _queries.UpdateApiKeyUsage(apiKey.Id);

        return apiKey;
    }

    /// <summary>
    /// Revoke an API key.
    /// </summary>
    public void RevokeApiKey(ApiKeyId id)
    {
        var user = _teamManagement.GetCurrentUser();
        var apiKey = _queries.GetApiKey(id)
            ?? throw new InvalidOperationException("API key not found");

        if (apiKey.OwnerId != user.Id)
            throw new UnauthorizedAccessException("Not authorized to revoke this API key");

        _queries.RevokeApiKey(id);
    }

    // ========================================================================
    // Sync Jobs
    // ========================================================================

    /// <summary>
    /// Start a sync job.
    /// </summary>
    public SyncJob StartSync(
        IntegrationInstanceId instanceId,
        SyncDirection direction,
        string resourceType,
        int totalRecords)
    {
        var user = _teamManagement.GetCurrentUser();
        var instance = _queries.GetInstance(instanceId)
            ?? throw new InvalidOperationException("Instance not found");

        if (instance.OwnerId != user.Id)
            throw new UnauthorizedAccessException("Not authorized to sync this instance");

        var job = new SyncJob(
            SyncJobId.New(),
            instanceId,
            direction,
            resourceType,
            SyncStatus.Running,
            DateTimeOffset.UtcNow,
            null,
            totalRecords,
            0,
            0,
            null,
            null
        );

        _queries.InsertSyncJob(job);

        LogIntegrationEvent(instanceId, IntegrationEventType.Synced,
            $"Sync started: {direction} {resourceType}", user.Id);

        return job;
    }

    /// <summary>
    /// Update sync job progress.
    /// </summary>
    public void UpdateSyncProgress(SyncJobId jobId, int processedRecords, int failedRecords)
    {
        _queries.UpdateSyncJobProgress(jobId, processedRecords, failedRecords);
    }

    /// <summary>
    /// Complete a sync job.
    /// </summary>
    public void CompleteSync(SyncJobId jobId, bool success, string? error = null)
    {
        var status = success ? SyncStatus.Completed : SyncStatus.Failed;
        _queries.CompleteSyncJob(jobId, status, error);

        var job = _queries.GetSyncJob(jobId)!;

        LogIntegrationEvent(job.InstanceId, IntegrationEventType.Synced,
            $"Sync completed: {status}", null);

        SyncCompleted?.Invoke(this, new SyncCompletedEventArgs
        {
            Job = job,
            Success = success
        });
    }

    /// <summary>
    /// Get sync history for an instance.
    /// </summary>
    public IReadOnlyList<SyncJob> GetSyncHistory(IntegrationInstanceId instanceId, int limit = 20)
    {
        return _queries.GetInstanceSyncJobs(instanceId, limit);
    }

    // ========================================================================
    // Event History
    // ========================================================================

    /// <summary>
    /// Get integration events for an instance.
    /// </summary>
    public IReadOnlyList<IntegrationEvent> GetEvents(IntegrationInstanceId instanceId, int limit = 50)
    {
        return _queries.GetInstanceEvents(instanceId, limit);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void LogIntegrationEvent(
        IntegrationInstanceId instanceId,
        IntegrationEventType eventType,
        string description,
        UserId? triggeredBy,
        Dictionary<string, object>? data = null)
    {
        var evt = new IntegrationEvent(
            Guid.NewGuid(),
            instanceId,
            eventType,
            description,
            triggeredBy,
            DateTimeOffset.UtcNow,
            data
        );

        _queries.InsertIntegrationEvent(evt);
    }

    private static string GenerateWebhookSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "cr_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashApiKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IReadOnlyList<Integration> GetBuiltInIntegrationDefinitions()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            // Cloud Providers
            CreateBuiltInIntegration("aws", "Amazon Web Services", "AWS cloud services integration",
                IntegrationCategory.CloudProvider, AuthMethod.AWS_IAM,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["list_instances", "create_instance", "terminate_instance", "get_metrics"],
                    [WebhookEventType.ResourceCreated, WebhookEventType.ResourceDeleted, WebhookEventType.CostAlert]),
                new IntegrationConfig(
                    [
                        new ConfigField("access_key_id", "Access Key ID", "AWS Access Key ID", ConfigFieldType.Secret, true, true, null, null, null),
                        new ConfigField("secret_access_key", "Secret Access Key", "AWS Secret Access Key", ConfigFieldType.Secret, true, true, null, null, null),
                        new ConfigField("region", "Region", "AWS Region", ConfigFieldType.Select, true, false, "us-east-1", null,
                            ["us-east-1", "us-west-2", "eu-west-1", "ap-southeast-1"])
                    ],
                    [], null, null, null, "https://aws.amazon.com", new(), 1000, 30000),
                now),

            CreateBuiltInIntegration("azure", "Microsoft Azure", "Azure cloud services integration",
                IntegrationCategory.CloudProvider, AuthMethod.Azure_ServicePrincipal,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["list_resources", "create_resource", "delete_resource", "get_metrics"],
                    [WebhookEventType.ResourceCreated, WebhookEventType.ResourceDeleted, WebhookEventType.SecurityAlert]),
                new IntegrationConfig(
                    [
                        new ConfigField("tenant_id", "Tenant ID", "Azure AD Tenant ID", ConfigFieldType.String, true, false, null, null, null),
                        new ConfigField("client_id", "Client ID", "Service Principal Client ID", ConfigFieldType.String, true, false, null, null, null),
                        new ConfigField("client_secret", "Client Secret", "Service Principal Secret", ConfigFieldType.Secret, true, true, null, null, null),
                        new ConfigField("subscription_id", "Subscription ID", "Azure Subscription ID", ConfigFieldType.String, true, false, null, null, null)
                    ],
                    [], null, null, null, "https://portal.azure.com", new(), 1000, 30000),
                now),

            CreateBuiltInIntegration("gcp", "Google Cloud Platform", "GCP cloud services integration",
                IntegrationCategory.CloudProvider, AuthMethod.GCP_ServiceAccount,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["list_instances", "create_instance", "delete_instance", "get_metrics"],
                    [WebhookEventType.ResourceCreated, WebhookEventType.ResourceDeleted]),
                new IntegrationConfig(
                    [
                        new ConfigField("project_id", "Project ID", "GCP Project ID", ConfigFieldType.String, true, false, null, null, null),
                        new ConfigField("service_account_key", "Service Account Key", "JSON key file contents", ConfigFieldType.Secret, true, true, null, null, null)
                    ],
                    [], null, null, null, "https://console.cloud.google.com", new(), 1000, 30000),
                now),

            // Source Control
            CreateBuiltInIntegration("github", "GitHub", "GitHub source control integration",
                IntegrationCategory.SourceControl, AuthMethod.OAuth2,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["create_issue", "create_pr", "list_repos", "get_commits"],
                    [WebhookEventType.Push, WebhookEventType.PullRequest, WebhookEventType.PullRequestMerged, WebhookEventType.Release]),
                new IntegrationConfig(
                    [],
                    ["repo", "read:org", "read:user"],
                    "https://github.com/login/oauth/authorize",
                    "https://github.com/login/oauth/access_token",
                    "https://api.controlroom.io/oauth/callback/github",
                    "https://api.github.com",
                    new() { ["Accept"] = "application/vnd.github+json" },
                    5000, 30000),
                now),

            CreateBuiltInIntegration("gitlab", "GitLab", "GitLab source control integration",
                IntegrationCategory.SourceControl, AuthMethod.OAuth2,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["create_issue", "create_mr", "list_projects", "get_commits"],
                    [WebhookEventType.Push, WebhookEventType.PullRequest, WebhookEventType.PullRequestMerged, WebhookEventType.Release]),
                new IntegrationConfig(
                    [
                        new ConfigField("gitlab_url", "GitLab URL", "GitLab instance URL", ConfigFieldType.Url, false, false, "https://gitlab.com", null, null)
                    ],
                    ["api", "read_user", "read_repository"],
                    "https://gitlab.com/oauth/authorize",
                    "https://gitlab.com/oauth/token",
                    "https://api.controlroom.io/oauth/callback/gitlab",
                    "https://gitlab.com/api/v4",
                    new(), 2000, 30000),
                now),

            // Issue Tracking
            CreateBuiltInIntegration("jira", "Jira", "Atlassian Jira integration",
                IntegrationCategory.IssueTracking, AuthMethod.OAuth2,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["create_issue", "update_issue", "transition_issue", "add_comment"],
                    [WebhookEventType.IssueCreated, WebhookEventType.IssueUpdated, WebhookEventType.IssueClosed, WebhookEventType.SprintStarted]),
                new IntegrationConfig(
                    [
                        new ConfigField("site_url", "Site URL", "Jira Cloud site URL", ConfigFieldType.Url, true, false, null, null, null)
                    ],
                    ["read:jira-work", "write:jira-work", "read:jira-user"],
                    "https://auth.atlassian.com/authorize",
                    "https://auth.atlassian.com/oauth/token",
                    "https://api.controlroom.io/oauth/callback/jira",
                    null, new(), 1000, 30000),
                now),

            CreateBuiltInIntegration("linear", "Linear", "Linear project management integration",
                IntegrationCategory.IssueTracking, AuthMethod.OAuth2,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["create_issue", "update_issue", "list_issues", "add_comment"],
                    [WebhookEventType.IssueCreated, WebhookEventType.IssueUpdated, WebhookEventType.IssueClosed]),
                new IntegrationConfig(
                    [],
                    ["read", "write", "issues:create"],
                    "https://linear.app/oauth/authorize",
                    "https://api.linear.app/oauth/token",
                    "https://api.controlroom.io/oauth/callback/linear",
                    "https://api.linear.app/graphql",
                    new(), 1500, 30000),
                now),

            // Alerting
            CreateBuiltInIntegration("pagerduty", "PagerDuty", "PagerDuty incident management integration",
                IntegrationCategory.Alerting, AuthMethod.ApiKey,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["create_incident", "acknowledge_incident", "resolve_incident", "list_incidents"],
                    [WebhookEventType.IncidentCreated, WebhookEventType.IncidentResolved, WebhookEventType.AlertTriggered]),
                new IntegrationConfig(
                    [
                        new ConfigField("api_key", "API Key", "PagerDuty API Key", ConfigFieldType.Secret, true, true, null, null, null),
                        new ConfigField("routing_key", "Routing Key", "Events API routing key", ConfigFieldType.Secret, false, true, null, null, null)
                    ],
                    [], null, null, null, "https://api.pagerduty.com",
                    new() { ["Content-Type"] = "application/json" }, 500, 30000),
                now),

            CreateBuiltInIntegration("opsgenie", "OpsGenie", "Atlassian OpsGenie integration",
                IntegrationCategory.Alerting, AuthMethod.ApiKey,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["create_alert", "close_alert", "acknowledge_alert", "list_alerts"],
                    [WebhookEventType.AlertTriggered, WebhookEventType.AlertResolved]),
                new IntegrationConfig(
                    [
                        new ConfigField("api_key", "API Key", "OpsGenie API Key", ConfigFieldType.Secret, true, true, null, null, null)
                    ],
                    [], null, null, null, "https://api.opsgenie.com/v2",
                    new(), 500, 30000),
                now),

            // Communication
            CreateBuiltInIntegration("slack", "Slack", "Slack messaging integration",
                IntegrationCategory.Communication, AuthMethod.OAuth2,
                new IntegrationCapabilities(true, true, false, false, true, true, true,
                    ["send_message", "send_file", "list_channels", "create_channel"],
                    []),
                new IntegrationConfig(
                    [],
                    ["channels:read", "chat:write", "files:write", "users:read"],
                    "https://slack.com/oauth/v2/authorize",
                    "https://slack.com/api/oauth.v2.access",
                    "https://api.controlroom.io/oauth/callback/slack",
                    "https://slack.com/api",
                    new(), 50, 30000),
                now),

            CreateBuiltInIntegration("teams", "Microsoft Teams", "Microsoft Teams integration",
                IntegrationCategory.Communication, AuthMethod.OAuth2,
                new IntegrationCapabilities(true, true, false, false, true, true, true,
                    ["send_message", "create_channel", "list_teams"],
                    []),
                new IntegrationConfig(
                    [],
                    ["ChannelMessage.Send", "Channel.ReadBasic.All", "Team.ReadBasic.All"],
                    "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                    "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                    "https://api.controlroom.io/oauth/callback/teams",
                    "https://graph.microsoft.com/v1.0",
                    new(), 500, 30000),
                now),

            // Monitoring
            CreateBuiltInIntegration("datadog", "Datadog", "Datadog monitoring integration",
                IntegrationCategory.Monitoring, AuthMethod.ApiKey,
                new IntegrationCapabilities(true, true, true, true, true, true, true,
                    ["query_metrics", "create_dashboard", "create_monitor"],
                    [WebhookEventType.AlertTriggered, WebhookEventType.AlertResolved]),
                new IntegrationConfig(
                    [
                        new ConfigField("api_key", "API Key", "Datadog API Key", ConfigFieldType.Secret, true, true, null, null, null),
                        new ConfigField("app_key", "Application Key", "Datadog Application Key", ConfigFieldType.Secret, true, true, null, null, null),
                        new ConfigField("site", "Site", "Datadog site", ConfigFieldType.Select, false, false, "datadoghq.com", null,
                            ["datadoghq.com", "datadoghq.eu", "us3.datadoghq.com", "us5.datadoghq.com"])
                    ],
                    [], null, null, null, "https://api.datadoghq.com",
                    new(), 300, 30000),
                now),

            CreateBuiltInIntegration("prometheus", "Prometheus", "Prometheus metrics integration",
                IntegrationCategory.Monitoring, AuthMethod.BasicAuth,
                new IntegrationCapabilities(false, false, true, true, false, true, true,
                    ["query", "query_range", "list_series"],
                    []),
                new IntegrationConfig(
                    [
                        new ConfigField("url", "Prometheus URL", "Prometheus server URL", ConfigFieldType.Url, true, false, null, null, null),
                        new ConfigField("username", "Username", "Basic auth username (optional)", ConfigFieldType.String, false, false, null, null, null),
                        new ConfigField("password", "Password", "Basic auth password (optional)", ConfigFieldType.Secret, false, true, null, null, null)
                    ],
                    [], null, null, null, null, new(), 100, 30000),
                now)
        ];
    }

    private static Integration CreateBuiltInIntegration(
        string name,
        string displayName,
        string description,
        IntegrationCategory category,
        AuthMethod authMethod,
        IntegrationCapabilities capabilities,
        IntegrationConfig config,
        DateTimeOffset createdAt)
    {
        return new Integration(
            IntegrationId.New(),
            name,
            displayName,
            description,
            category,
            authMethod,
            $"https://controlroom.io/icons/{name}.svg",
            $"https://docs.controlroom.io/integrations/{name}",
            true,
            true,
            capabilities,
            config,
            createdAt,
            null
        );
    }
}

// ========================================================================
// Event Args
// ========================================================================

public sealed class IntegrationConnectedEventArgs : EventArgs
{
    public required IntegrationInstance Instance { get; init; }
}

public sealed class IntegrationDisconnectedEventArgs : EventArgs
{
    public required IntegrationInstanceId InstanceId { get; init; }
}

public sealed class WebhookReceivedEventArgs : EventArgs
{
    public required WebhookId WebhookId { get; init; }
    public required WebhookEventType EventType { get; init; }
    public required string Payload { get; init; }
}

public sealed class SyncCompletedEventArgs : EventArgs
{
    public required SyncJob Job { get; init; }
    public required bool Success { get; init; }
}
