using System.Text.Json;
using Microsoft.Data.Sqlite;
using ControlRoom.Domain.Model;

namespace ControlRoom.Infrastructure.Storage.Queries;

/// <summary>
/// Database queries for integrations.
/// </summary>
public sealed class IntegrationQueries
{
    private readonly Db _db;

    public IntegrationQueries(Db db)
    {
        _db = db;
    }

    // ========================================================================
    // Integration Definitions
    // ========================================================================

    public void InsertIntegration(Integration integration)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO integrations (id, name, display_name, description, category, auth_method,
                icon_url, documentation_url, is_built_in, is_enabled, capabilities_json, config_json, created_at, updated_at)
            VALUES ($id, $name, $display_name, $description, $category, $auth_method,
                $icon_url, $documentation_url, $is_built_in, $is_enabled, $capabilities_json, $config_json, $created_at, $updated_at)
            """;
        cmd.Parameters.AddWithValue("$id", integration.Id.ToString());
        cmd.Parameters.AddWithValue("$name", integration.Name);
        cmd.Parameters.AddWithValue("$display_name", integration.DisplayName);
        cmd.Parameters.AddWithValue("$description", integration.Description);
        cmd.Parameters.AddWithValue("$category", integration.Category.ToString());
        cmd.Parameters.AddWithValue("$auth_method", integration.AuthMethod.ToString());
        cmd.Parameters.AddWithValue("$icon_url", integration.IconUrl ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$documentation_url", integration.DocumentationUrl ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$is_built_in", integration.IsBuiltIn ? 1 : 0);
        cmd.Parameters.AddWithValue("$is_enabled", integration.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$capabilities_json", JsonSerializer.Serialize(integration.Capabilities));
        cmd.Parameters.AddWithValue("$config_json", JsonSerializer.Serialize(integration.Config));
        cmd.Parameters.AddWithValue("$created_at", integration.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated_at", integration.UpdatedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public Integration? GetIntegration(IntegrationId id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM integrations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapIntegration(r);
    }

    public Integration? GetIntegrationByName(string name)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM integrations WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapIntegration(r);
    }

    public IReadOnlyList<Integration> GetIntegrations(IntegrationCategory? category = null, bool enabledOnly = true)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();

        var sql = "SELECT * FROM integrations WHERE 1=1";
        if (category.HasValue)
        {
            sql += " AND category = $category";
            cmd.Parameters.AddWithValue("$category", category.Value.ToString());
        }
        if (enabledOnly)
        {
            sql += " AND is_enabled = 1";
        }
        sql += " ORDER BY display_name";

        cmd.CommandText = sql;

        var list = new List<Integration>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapIntegration(r));
        }
        return list;
    }

    public void UpdateIntegration(Integration integration)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE integrations SET
                display_name = $display_name,
                description = $description,
                is_enabled = $is_enabled,
                capabilities_json = $capabilities_json,
                config_json = $config_json,
                updated_at = $updated_at
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", integration.Id.ToString());
        cmd.Parameters.AddWithValue("$display_name", integration.DisplayName);
        cmd.Parameters.AddWithValue("$description", integration.Description);
        cmd.Parameters.AddWithValue("$is_enabled", integration.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$capabilities_json", JsonSerializer.Serialize(integration.Capabilities));
        cmd.Parameters.AddWithValue("$config_json", JsonSerializer.Serialize(integration.Config));
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    // ========================================================================
    // Integration Instances
    // ========================================================================

    public void InsertInstance(IntegrationInstance instance)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO integration_instances (id, integration_id, owner_id, team_id, name, display_name,
                status, health, configuration_json, credentials_json, created_at, connected_at,
                last_sync_at, last_health_check_at, last_error, metadata_json)
            VALUES ($id, $integration_id, $owner_id, $team_id, $name, $display_name,
                $status, $health, $configuration_json, $credentials_json, $created_at, $connected_at,
                $last_sync_at, $last_health_check_at, $last_error, $metadata_json)
            """;
        cmd.Parameters.AddWithValue("$id", instance.Id.ToString());
        cmd.Parameters.AddWithValue("$integration_id", instance.IntegrationId.ToString());
        cmd.Parameters.AddWithValue("$owner_id", instance.OwnerId.ToString());
        cmd.Parameters.AddWithValue("$team_id", instance.TeamId?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$name", instance.Name);
        cmd.Parameters.AddWithValue("$display_name", instance.DisplayName);
        cmd.Parameters.AddWithValue("$status", instance.Status.ToString());
        cmd.Parameters.AddWithValue("$health", instance.Health.ToString());
        cmd.Parameters.AddWithValue("$configuration_json", JsonSerializer.Serialize(instance.Configuration));
        cmd.Parameters.AddWithValue("$credentials_json", instance.Credentials != null ? JsonSerializer.Serialize(instance.Credentials) : DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", instance.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$connected_at", instance.ConnectedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$last_sync_at", instance.LastSyncAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$last_health_check_at", instance.LastHealthCheckAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$last_error", instance.LastError ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata_json", instance.Metadata != null ? JsonSerializer.Serialize(instance.Metadata) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IntegrationInstance? GetInstance(IntegrationInstanceId id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM integration_instances WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapInstance(r);
    }

    public IReadOnlyList<IntegrationInstance> GetUserInstances(UserId userId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM integration_instances
            WHERE owner_id = $owner_id
            ORDER BY display_name
            """;
        cmd.Parameters.AddWithValue("$owner_id", userId.ToString());

        var list = new List<IntegrationInstance>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapInstance(r));
        }
        return list;
    }

    public IReadOnlyList<IntegrationInstance> GetTeamInstances(TeamId teamId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM integration_instances
            WHERE team_id = $team_id
            ORDER BY display_name
            """;
        cmd.Parameters.AddWithValue("$team_id", teamId.ToString());

        var list = new List<IntegrationInstance>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapInstance(r));
        }
        return list;
    }

    public IReadOnlyList<IntegrationInstance> GetInstancesByIntegration(IntegrationId integrationId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM integration_instances
            WHERE integration_id = $integration_id
            ORDER BY display_name
            """;
        cmd.Parameters.AddWithValue("$integration_id", integrationId.ToString());

        var list = new List<IntegrationInstance>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapInstance(r));
        }
        return list;
    }

    public void UpdateInstanceStatus(IntegrationInstanceId id, IntegrationStatus status, string? error = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE integration_instances SET
                status = $status,
                last_error = $error,
                connected_at = CASE WHEN $status = 'Connected' THEN $now ELSE connected_at END
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void UpdateInstanceHealth(IntegrationInstanceId id, IntegrationHealth health)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE integration_instances SET
                health = $health,
                last_health_check_at = $now
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$health", health.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void UpdateInstanceCredentials(IntegrationInstanceId id, CredentialInfo credentials)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE integration_instances SET
                credentials_json = $credentials_json
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$credentials_json", JsonSerializer.Serialize(credentials));
        cmd.ExecuteNonQuery();
    }

    public void DeleteInstance(IntegrationInstanceId id)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Delete related data first
        Exec(conn, tx, "DELETE FROM webhook_events WHERE instance_id = $id", ("$id", id.ToString()));
        Exec(conn, tx, "DELETE FROM webhook_endpoints WHERE instance_id = $id", ("$id", id.ToString()));
        Exec(conn, tx, "DELETE FROM sync_jobs WHERE instance_id = $id", ("$id", id.ToString()));
        Exec(conn, tx, "DELETE FROM integration_events WHERE instance_id = $id", ("$id", id.ToString()));
        Exec(conn, tx, "DELETE FROM integration_instances WHERE id = $id", ("$id", id.ToString()));

        tx.Commit();
    }

    // ========================================================================
    // Webhooks
    // ========================================================================

    public void InsertWebhook(WebhookEndpoint webhook)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO webhook_endpoints (id, instance_id, url, secret, subscribed_events, is_active,
                created_at, last_received_at, total_received, total_processed, total_failed)
            VALUES ($id, $instance_id, $url, $secret, $subscribed_events, $is_active,
                $created_at, $last_received_at, $total_received, $total_processed, $total_failed)
            """;
        cmd.Parameters.AddWithValue("$id", webhook.Id.ToString());
        cmd.Parameters.AddWithValue("$instance_id", webhook.InstanceId.ToString());
        cmd.Parameters.AddWithValue("$url", webhook.Url);
        cmd.Parameters.AddWithValue("$secret", webhook.Secret);
        cmd.Parameters.AddWithValue("$subscribed_events", JsonSerializer.Serialize(webhook.SubscribedEvents.Select(e => e.ToString())));
        cmd.Parameters.AddWithValue("$is_active", webhook.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$created_at", webhook.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last_received_at", webhook.LastReceivedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$total_received", webhook.TotalReceived);
        cmd.Parameters.AddWithValue("$total_processed", webhook.TotalProcessed);
        cmd.Parameters.AddWithValue("$total_failed", webhook.TotalFailed);
        cmd.ExecuteNonQuery();
    }

    public WebhookEndpoint? GetWebhook(WebhookId id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM webhook_endpoints WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapWebhook(r);
    }

    public IReadOnlyList<WebhookEndpoint> GetInstanceWebhooks(IntegrationInstanceId instanceId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM webhook_endpoints WHERE instance_id = $instance_id ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$instance_id", instanceId.ToString());

        var list = new List<WebhookEndpoint>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapWebhook(r));
        }
        return list;
    }

    public void IncrementWebhookCounters(WebhookId id, bool success)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = success
            ? "UPDATE webhook_endpoints SET total_received = total_received + 1, total_processed = total_processed + 1, last_received_at = $now WHERE id = $id"
            : "UPDATE webhook_endpoints SET total_received = total_received + 1, total_failed = total_failed + 1, last_received_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    // ========================================================================
    // Webhook Events
    // ========================================================================

    public void InsertWebhookEvent(WebhookEvent evt)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO webhook_events (id, webhook_id, instance_id, event_type, raw_payload, headers_json,
                received_at, processed_at, is_processed, error)
            VALUES ($id, $webhook_id, $instance_id, $event_type, $raw_payload, $headers_json,
                $received_at, $processed_at, $is_processed, $error)
            """;
        cmd.Parameters.AddWithValue("$id", evt.Id.ToString());
        cmd.Parameters.AddWithValue("$webhook_id", evt.WebhookId.ToString());
        cmd.Parameters.AddWithValue("$instance_id", evt.InstanceId.ToString());
        cmd.Parameters.AddWithValue("$event_type", evt.EventType.ToString());
        cmd.Parameters.AddWithValue("$raw_payload", evt.RawPayload);
        cmd.Parameters.AddWithValue("$headers_json", JsonSerializer.Serialize(evt.Headers));
        cmd.Parameters.AddWithValue("$received_at", evt.ReceivedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$processed_at", evt.ProcessedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$is_processed", evt.IsProcessed ? 1 : 0);
        cmd.Parameters.AddWithValue("$error", evt.Error ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<WebhookEvent> GetUnprocessedEvents(int limit = 100)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM webhook_events
            WHERE is_processed = 0
            ORDER BY received_at ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<WebhookEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapWebhookEvent(r));
        }
        return list;
    }

    public void MarkEventProcessed(Guid eventId, string? error = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE webhook_events SET
                is_processed = 1,
                processed_at = $now,
                error = $error
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", eventId.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ========================================================================
    // API Keys
    // ========================================================================

    public void InsertApiKey(ApiKey apiKey)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO api_keys (id, owner_id, team_id, name, key_prefix, hashed_key, scopes,
                created_at, expires_at, last_used_at, is_active, allowed_ips, usage_count)
            VALUES ($id, $owner_id, $team_id, $name, $key_prefix, $hashed_key, $scopes,
                $created_at, $expires_at, $last_used_at, $is_active, $allowed_ips, $usage_count)
            """;
        cmd.Parameters.AddWithValue("$id", apiKey.Id.ToString());
        cmd.Parameters.AddWithValue("$owner_id", apiKey.OwnerId.ToString());
        cmd.Parameters.AddWithValue("$team_id", apiKey.TeamId?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$name", apiKey.Name);
        cmd.Parameters.AddWithValue("$key_prefix", apiKey.KeyPrefix);
        cmd.Parameters.AddWithValue("$hashed_key", apiKey.HashedKey);
        cmd.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(apiKey.Scopes.Select(s => s.ToString())));
        cmd.Parameters.AddWithValue("$created_at", apiKey.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$expires_at", apiKey.ExpiresAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$last_used_at", apiKey.LastUsedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$is_active", apiKey.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$allowed_ips", apiKey.AllowedIps ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$usage_count", apiKey.UsageCount);
        cmd.ExecuteNonQuery();
    }

    public ApiKey? GetApiKey(ApiKeyId id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM api_keys WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapApiKey(r);
    }

    public ApiKey? GetApiKeyByPrefix(string prefix)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM api_keys WHERE key_prefix = $prefix AND is_active = 1";
        cmd.Parameters.AddWithValue("$prefix", prefix);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapApiKey(r);
    }

    public IReadOnlyList<ApiKey> GetUserApiKeys(UserId userId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM api_keys WHERE owner_id = $owner_id ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$owner_id", userId.ToString());

        var list = new List<ApiKey>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapApiKey(r));
        }
        return list;
    }

    public void UpdateApiKeyUsage(ApiKeyId id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE api_keys SET
                last_used_at = $now,
                usage_count = usage_count + 1
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void RevokeApiKey(ApiKeyId id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE api_keys SET is_active = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    // ========================================================================
    // Sync Jobs
    // ========================================================================

    public void InsertSyncJob(SyncJob job)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_jobs (id, instance_id, direction, resource_type, status, started_at,
                completed_at, total_records, processed_records, failed_records, error, metadata_json)
            VALUES ($id, $instance_id, $direction, $resource_type, $status, $started_at,
                $completed_at, $total_records, $processed_records, $failed_records, $error, $metadata_json)
            """;
        cmd.Parameters.AddWithValue("$id", job.Id.ToString());
        cmd.Parameters.AddWithValue("$instance_id", job.InstanceId.ToString());
        cmd.Parameters.AddWithValue("$direction", job.Direction.ToString());
        cmd.Parameters.AddWithValue("$resource_type", job.ResourceType);
        cmd.Parameters.AddWithValue("$status", job.Status.ToString());
        cmd.Parameters.AddWithValue("$started_at", job.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completed_at", job.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$total_records", job.TotalRecords);
        cmd.Parameters.AddWithValue("$processed_records", job.ProcessedRecords);
        cmd.Parameters.AddWithValue("$failed_records", job.FailedRecords);
        cmd.Parameters.AddWithValue("$error", job.Error ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata_json", job.Metadata != null ? JsonSerializer.Serialize(job.Metadata) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public SyncJob? GetSyncJob(SyncJobId id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sync_jobs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapSyncJob(r);
    }

    public IReadOnlyList<SyncJob> GetInstanceSyncJobs(IntegrationInstanceId instanceId, int limit = 20)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM sync_jobs
            WHERE instance_id = $instance_id
            ORDER BY started_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$instance_id", instanceId.ToString());
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<SyncJob>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapSyncJob(r));
        }
        return list;
    }

    public void UpdateSyncJobProgress(SyncJobId id, int processedRecords, int failedRecords)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sync_jobs SET
                processed_records = $processed_records,
                failed_records = $failed_records
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$processed_records", processedRecords);
        cmd.Parameters.AddWithValue("$failed_records", failedRecords);
        cmd.ExecuteNonQuery();
    }

    public void CompleteSyncJob(SyncJobId id, SyncStatus status, string? error = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sync_jobs SET
                status = $status,
                completed_at = $now,
                error = $error
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ========================================================================
    // Integration Events
    // ========================================================================

    public void InsertIntegrationEvent(IntegrationEvent evt)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO integration_events (id, instance_id, event_type, description, triggered_by, occurred_at, data_json)
            VALUES ($id, $instance_id, $event_type, $description, $triggered_by, $occurred_at, $data_json)
            """;
        cmd.Parameters.AddWithValue("$id", evt.Id.ToString());
        cmd.Parameters.AddWithValue("$instance_id", evt.InstanceId.ToString());
        cmd.Parameters.AddWithValue("$event_type", evt.EventType.ToString());
        cmd.Parameters.AddWithValue("$description", evt.Description);
        cmd.Parameters.AddWithValue("$triggered_by", evt.TriggeredBy?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$occurred_at", evt.OccurredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$data_json", evt.Data != null ? JsonSerializer.Serialize(evt.Data) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<IntegrationEvent> GetInstanceEvents(IntegrationInstanceId instanceId, int limit = 50)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM integration_events
            WHERE instance_id = $instance_id
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$instance_id", instanceId.ToString());
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<IntegrationEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapIntegrationEvent(r));
        }
        return list;
    }

    // ========================================================================
    // Mapping Helpers
    // ========================================================================

    private static Integration MapIntegration(SqliteDataReader r)
    {
        return new Integration(
            IntegrationId.Parse(r.GetString(r.GetOrdinal("id"))),
            r.GetString(r.GetOrdinal("name")),
            r.GetString(r.GetOrdinal("display_name")),
            r.GetString(r.GetOrdinal("description")),
            Enum.Parse<IntegrationCategory>(r.GetString(r.GetOrdinal("category"))),
            Enum.Parse<AuthMethod>(r.GetString(r.GetOrdinal("auth_method"))),
            r.IsDBNull(r.GetOrdinal("icon_url")) ? "" : r.GetString(r.GetOrdinal("icon_url")),
            r.IsDBNull(r.GetOrdinal("documentation_url")) ? "" : r.GetString(r.GetOrdinal("documentation_url")),
            r.GetInt32(r.GetOrdinal("is_built_in")) == 1,
            r.GetInt32(r.GetOrdinal("is_enabled")) == 1,
            JsonSerializer.Deserialize<IntegrationCapabilities>(r.GetString(r.GetOrdinal("capabilities_json")))!,
            JsonSerializer.Deserialize<IntegrationConfig>(r.GetString(r.GetOrdinal("config_json")))!,
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
            r.IsDBNull(r.GetOrdinal("updated_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at")))
        );
    }

    private static IntegrationInstance MapInstance(SqliteDataReader r)
    {
        return new IntegrationInstance(
            IntegrationInstanceId.Parse(r.GetString(r.GetOrdinal("id"))),
            IntegrationId.Parse(r.GetString(r.GetOrdinal("integration_id"))),
            new UserId(Guid.Parse(r.GetString(r.GetOrdinal("owner_id")))),
            r.IsDBNull(r.GetOrdinal("team_id")) ? null : new TeamId(Guid.Parse(r.GetString(r.GetOrdinal("team_id")))),
            r.GetString(r.GetOrdinal("name")),
            r.GetString(r.GetOrdinal("display_name")),
            Enum.Parse<IntegrationStatus>(r.GetString(r.GetOrdinal("status"))),
            Enum.Parse<IntegrationHealth>(r.GetString(r.GetOrdinal("health"))),
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(r.GetOrdinal("configuration_json")))!,
            r.IsDBNull(r.GetOrdinal("credentials_json")) ? null : JsonSerializer.Deserialize<CredentialInfo>(r.GetString(r.GetOrdinal("credentials_json"))),
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
            r.IsDBNull(r.GetOrdinal("connected_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("connected_at"))),
            r.IsDBNull(r.GetOrdinal("last_sync_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("last_sync_at"))),
            r.IsDBNull(r.GetOrdinal("last_health_check_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("last_health_check_at"))),
            r.IsDBNull(r.GetOrdinal("last_error")) ? null : r.GetString(r.GetOrdinal("last_error")),
            r.IsDBNull(r.GetOrdinal("metadata_json")) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(r.GetString(r.GetOrdinal("metadata_json")))
        );
    }

    private static WebhookEndpoint MapWebhook(SqliteDataReader r)
    {
        var eventsJson = r.GetString(r.GetOrdinal("subscribed_events"));
        var eventStrings = JsonSerializer.Deserialize<List<string>>(eventsJson) ?? [];
        var events = eventStrings.Select(e => Enum.Parse<WebhookEventType>(e)).ToList();

        return new WebhookEndpoint(
            WebhookId.Parse(r.GetString(r.GetOrdinal("id"))),
            IntegrationInstanceId.Parse(r.GetString(r.GetOrdinal("instance_id"))),
            r.GetString(r.GetOrdinal("url")),
            r.GetString(r.GetOrdinal("secret")),
            events,
            r.GetInt32(r.GetOrdinal("is_active")) == 1,
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
            r.IsDBNull(r.GetOrdinal("last_received_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("last_received_at"))),
            r.GetInt32(r.GetOrdinal("total_received")),
            r.GetInt32(r.GetOrdinal("total_processed")),
            r.GetInt32(r.GetOrdinal("total_failed"))
        );
    }

    private static WebhookEvent MapWebhookEvent(SqliteDataReader r)
    {
        return new WebhookEvent(
            Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            WebhookId.Parse(r.GetString(r.GetOrdinal("webhook_id"))),
            IntegrationInstanceId.Parse(r.GetString(r.GetOrdinal("instance_id"))),
            Enum.Parse<WebhookEventType>(r.GetString(r.GetOrdinal("event_type"))),
            r.GetString(r.GetOrdinal("raw_payload")),
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(r.GetOrdinal("headers_json")))!,
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("received_at"))),
            r.IsDBNull(r.GetOrdinal("processed_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("processed_at"))),
            r.GetInt32(r.GetOrdinal("is_processed")) == 1,
            r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error"))
        );
    }

    private static ApiKey MapApiKey(SqliteDataReader r)
    {
        var scopesJson = r.GetString(r.GetOrdinal("scopes"));
        var scopeStrings = JsonSerializer.Deserialize<List<string>>(scopesJson) ?? [];
        var scopes = scopeStrings.Select(s => Enum.Parse<ApiKeyScope>(s)).ToList();

        return new ApiKey(
            ApiKeyId.Parse(r.GetString(r.GetOrdinal("id"))),
            new UserId(Guid.Parse(r.GetString(r.GetOrdinal("owner_id")))),
            r.IsDBNull(r.GetOrdinal("team_id")) ? null : new TeamId(Guid.Parse(r.GetString(r.GetOrdinal("team_id")))),
            r.GetString(r.GetOrdinal("name")),
            r.GetString(r.GetOrdinal("key_prefix")),
            r.GetString(r.GetOrdinal("hashed_key")),
            scopes,
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
            r.IsDBNull(r.GetOrdinal("expires_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("expires_at"))),
            r.IsDBNull(r.GetOrdinal("last_used_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("last_used_at"))),
            r.GetInt32(r.GetOrdinal("is_active")) == 1,
            r.IsDBNull(r.GetOrdinal("allowed_ips")) ? null : r.GetString(r.GetOrdinal("allowed_ips")),
            r.GetInt32(r.GetOrdinal("usage_count"))
        );
    }

    private static SyncJob MapSyncJob(SqliteDataReader r)
    {
        return new SyncJob(
            SyncJobId.Parse(r.GetString(r.GetOrdinal("id"))),
            IntegrationInstanceId.Parse(r.GetString(r.GetOrdinal("instance_id"))),
            Enum.Parse<SyncDirection>(r.GetString(r.GetOrdinal("direction"))),
            r.GetString(r.GetOrdinal("resource_type")),
            Enum.Parse<SyncStatus>(r.GetString(r.GetOrdinal("status"))),
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("started_at"))),
            r.IsDBNull(r.GetOrdinal("completed_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("completed_at"))),
            r.GetInt32(r.GetOrdinal("total_records")),
            r.GetInt32(r.GetOrdinal("processed_records")),
            r.GetInt32(r.GetOrdinal("failed_records")),
            r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error")),
            r.IsDBNull(r.GetOrdinal("metadata_json")) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(r.GetString(r.GetOrdinal("metadata_json")))
        );
    }

    private static IntegrationEvent MapIntegrationEvent(SqliteDataReader r)
    {
        return new IntegrationEvent(
            Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            IntegrationInstanceId.Parse(r.GetString(r.GetOrdinal("instance_id"))),
            Enum.Parse<IntegrationEventType>(r.GetString(r.GetOrdinal("event_type"))),
            r.GetString(r.GetOrdinal("description")),
            r.IsDBNull(r.GetOrdinal("triggered_by")) ? null : new UserId(Guid.Parse(r.GetString(r.GetOrdinal("triggered_by")))),
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("occurred_at"))),
            r.IsDBNull(r.GetOrdinal("data_json")) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(r.GetString(r.GetOrdinal("data_json")))
        );
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in ps)
            cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }
}
