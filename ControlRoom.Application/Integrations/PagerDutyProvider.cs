using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlRoom.Application.Integrations;

/// <summary>
/// PagerDuty integration provider.
/// Supports incidents, services, schedules, and escalation policies.
/// </summary>
public sealed class PagerDutyProvider : IIncidentManagementProvider
{
    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private string? _userId;

    public string ProviderName => "pagerduty";
    public DevOpsCategory Category => DevOpsCategory.IncidentManagement;

    private const string BaseUrl = "https://api.pagerduty.com";

    public PagerDutyProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Configure with API key and user email.
    /// </summary>
    public void Configure(string apiKey, string? userEmail = null)
    {
        _apiKey = apiKey;
        // User ID is resolved later if needed
    }

    public async Task<DevOpsValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.TryGetValue("api_key", out var apiKey))
        {
            return new DevOpsValidationResult(
                false, "Missing required api_key", null, null, null, null, []);
        }

        Configure(apiKey, configuration.GetValueOrDefault("user_email"));

        try
        {
            var response = await MakeRequestAsync(HttpMethod.Get, "/users/me", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Try abilities endpoint for service accounts
                var abilitiesResponse = await MakeRequestAsync(HttpMethod.Get, "/abilities", cancellationToken);
                if (!abilitiesResponse.IsSuccessStatusCode)
                {
                    return new DevOpsValidationResult(
                        false, $"Authentication failed: {response.StatusCode}",
                        null, null, null, null, []);
                }

                var abilitiesContent = await abilitiesResponse.Content.ReadAsStringAsync(cancellationToken);
                var abilities = JsonSerializer.Deserialize<PagerDutyAbilitiesResponse>(abilitiesContent);

                return new DevOpsValidationResult(
                    true,
                    null,
                    null,
                    "Service Account",
                    null,
                    null,
                    abilities?.Abilities ?? []);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var userResponse = JsonSerializer.Deserialize<PagerDutyUserResponse>(content);
            var user = userResponse?.User;

            _userId = user?.Id;

            return new DevOpsValidationResult(
                true,
                null,
                user?.Id,
                user?.Name,
                null,
                null,
                ["read", "write"],
                new() { ["email"] = user?.Email ?? "" });
        }
        catch (Exception ex)
        {
            return new DevOpsValidationResult(
                false, $"Validation failed: {ex.Message}", null, null, null, null, []);
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await MakeRequestAsync(HttpMethod.Get, "/abilities", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DevOpsResourceList<Incident>> ListIncidentsAsync(
        IncidentQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var queryParams = new List<string>();

        if (query?.Statuses != null && query.Statuses.Count > 0)
        {
            foreach (var status in query.Statuses)
            {
                queryParams.Add($"statuses[]={MapIncidentStatus(status)}");
            }
        }

        if (query?.Urgency != null)
        {
            queryParams.Add($"urgencies[]={query.Urgency.Value.ToString().ToLowerInvariant()}");
        }

        if (query?.ServiceId != null)
        {
            queryParams.Add($"service_ids[]={query.ServiceId}");
        }

        if (query?.Since != null)
        {
            queryParams.Add($"since={query.Since.Value:yyyy-MM-ddTHH:mm:ssZ}");
        }

        if (query?.Until != null)
        {
            queryParams.Add($"until={query.Until.Value:yyyy-MM-ddTHH:mm:ssZ}");
        }

        var limit = query?.MaxResults ?? 25;
        queryParams.Add($"limit={limit}");

        var url = "/incidents" + (queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "");

        var response = await MakeRequestAsync(HttpMethod.Get, url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var incidentsResponse = JsonSerializer.Deserialize<PagerDutyIncidentsResponse>(content);

        return new DevOpsResourceList<Incident>(
            (incidentsResponse?.Incidents ?? []).Select(MapIncident).ToList(),
            incidentsResponse?.Total ?? 0,
            incidentsResponse?.More == true ? "next" : null,
            DateTimeOffset.UtcNow);
    }

    public async Task<Incident?> GetIncidentAsync(
        string incidentId,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(HttpMethod.Get, $"/incidents/{incidentId}", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var incidentResponse = JsonSerializer.Deserialize<PagerDutyIncidentResponse>(content);

        return incidentResponse?.Incident != null ? MapIncident(incidentResponse.Incident) : null;
    }

    public async Task<Incident> CreateIncidentAsync(
        CreateIncidentRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new
        {
            incident = new
            {
                type = "incident",
                title = request.Title,
                service = new { id = request.ServiceId, type = "service_reference" },
                urgency = request.Urgency.ToString().ToLowerInvariant(),
                body = request.Description != null ? new { type = "incident_body", details = request.Description } : null,
                escalation_policy = request.EscalationPolicyId != null
                    ? new { id = request.EscalationPolicyId, type = "escalation_policy_reference" }
                    : null
            }
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            "/incidents",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var incidentResponse = JsonSerializer.Deserialize<PagerDutyIncidentResponse>(content);

        return MapIncident(incidentResponse?.Incident
            ?? throw new InvalidOperationException("Failed to create incident"));
    }

    public async Task<Incident> AcknowledgeIncidentAsync(
        string incidentId,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        return await UpdateIncidentStatusAsync(incidentId, "acknowledged", message, cancellationToken);
    }

    public async Task<Incident> ResolveIncidentAsync(
        string incidentId,
        string? resolution = null,
        CancellationToken cancellationToken = default)
    {
        return await UpdateIncidentStatusAsync(incidentId, "resolved", resolution, cancellationToken);
    }

    public async Task<Incident> EscalateIncidentAsync(
        string incidentId,
        string escalationPolicyId,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new
        {
            incidents = new[]
            {
                new
                {
                    id = incidentId,
                    type = "incident_reference",
                    escalation_policy = new
                    {
                        id = escalationPolicyId,
                        type = "escalation_policy_reference"
                    }
                }
            }
        };

        var response = await MakeRequestAsync(
            HttpMethod.Put,
            "/incidents",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        return await GetIncidentAsync(incidentId, cancellationToken)
            ?? throw new InvalidOperationException("Failed to retrieve escalated incident");
    }

    public async Task<DevOpsResourceList<OnCallSchedule>> ListOnCallSchedulesAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(HttpMethod.Get, "/schedules?limit=100", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var schedulesResponse = JsonSerializer.Deserialize<PagerDutySchedulesResponse>(content);

        return new DevOpsResourceList<OnCallSchedule>(
            (schedulesResponse?.Schedules ?? []).Select(MapSchedule).ToList(),
            schedulesResponse?.Total ?? 0,
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<OnCallUser>> GetCurrentOnCallAsync(
        string scheduleId,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var now = DateTimeOffset.UtcNow;
        var since = now.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var until = now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ");

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/schedules/{scheduleId}/users?since={since}&until={until}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var usersResponse = JsonSerializer.Deserialize<PagerDutyScheduleUsersResponse>(content);

        return (usersResponse?.Users ?? []).Select((u, i) => new OnCallUser(
            u.Id ?? "",
            u.Name ?? "",
            u.Email,
            now,
            now.AddDays(1),
            i + 1)).ToList();
    }

    public async Task<DevOpsResourceList<IncidentService>> ListServicesAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(HttpMethod.Get, "/services?limit=100", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var servicesResponse = JsonSerializer.Deserialize<PagerDutyServicesResponse>(content);

        return new DevOpsResourceList<IncidentService>(
            (servicesResponse?.Services ?? []).Select(MapService).ToList(),
            servicesResponse?.Total ?? 0,
            null,
            DateTimeOffset.UtcNow);
    }

    // ========================================================================
    // PagerDuty-Specific Operations
    // ========================================================================

    /// <summary>
    /// Adds a note to an incident.
    /// </summary>
    public async Task<PagerDutyNote> AddIncidentNoteAsync(
        string incidentId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new
        {
            note = new
            {
                content
            }
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"/incidents/{incidentId}/notes",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var noteResponse = JsonSerializer.Deserialize<PagerDutyNoteResponse>(responseContent);

        return noteResponse?.Note
            ?? throw new InvalidOperationException("Failed to create note");
    }

    /// <summary>
    /// Lists escalation policies.
    /// </summary>
    public async Task<IReadOnlyList<PagerDutyEscalationPolicy>> ListEscalationPoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(HttpMethod.Get, "/escalation_policies?limit=100", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var policiesResponse = JsonSerializer.Deserialize<PagerDutyEscalationPoliciesResponse>(content);

        return policiesResponse?.EscalationPolicies ?? [];
    }

    /// <summary>
    /// Triggers an event via Events API v2.
    /// </summary>
    public async Task<string> TriggerEventAsync(
        string routingKey,
        string summary,
        string source,
        PagerDutySeverity severity = PagerDutySeverity.Error,
        string? dedupKey = null,
        Dictionary<string, object>? customDetails = null,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            routing_key = routingKey,
            event_action = "trigger",
            dedup_key = dedupKey,
            payload = new
            {
                summary,
                source,
                severity = severity.ToString().ToLowerInvariant(),
                custom_details = customDetails
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://events.pagerduty.com/v2/enqueue");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var eventResponse = JsonSerializer.Deserialize<PagerDutyEventResponse>(content);

        return eventResponse?.DedupKey ?? "";
    }

    /// <summary>
    /// Acknowledges an event via Events API v2.
    /// </summary>
    public async Task AcknowledgeEventAsync(
        string routingKey,
        string dedupKey,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            routing_key = routingKey,
            event_action = "acknowledge",
            dedup_key = dedupKey
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://events.pagerduty.com/v2/enqueue");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Resolves an event via Events API v2.
    /// </summary>
    public async Task ResolveEventAsync(
        string routingKey,
        string dedupKey,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            routing_key = routingKey,
            event_action = "resolve",
            dedup_key = dedupKey
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://events.pagerduty.com/v2/enqueue");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new InvalidOperationException("PagerDuty API key not configured. Call Configure() first.");
        }
    }

    private async Task<HttpResponseMessage> MakeRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken,
        string? body = null)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", $"token={_apiKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Add From header for write operations
        if (_userId != null && method != HttpMethod.Get)
        {
            request.Headers.Add("From", _userId);
        }

        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<Incident> UpdateIncidentStatusAsync(
        string incidentId,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();

        var body = new
        {
            incidents = new[]
            {
                new
                {
                    id = incidentId,
                    type = "incident_reference",
                    status
                }
            }
        };

        var response = await MakeRequestAsync(
            HttpMethod.Put,
            "/incidents",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        // Add note if message provided
        if (!string.IsNullOrEmpty(message))
        {
            await AddIncidentNoteAsync(incidentId, message, cancellationToken);
        }

        return await GetIncidentAsync(incidentId, cancellationToken)
            ?? throw new InvalidOperationException("Failed to retrieve updated incident");
    }

    private static string MapIncidentStatus(IncidentStatus status) => status switch
    {
        IncidentStatus.Triggered => "triggered",
        IncidentStatus.Acknowledged => "acknowledged",
        IncidentStatus.Resolved => "resolved",
        _ => "triggered"
    };

    private static Incident MapIncident(PagerDutyIncident incident)
    {
        var status = incident.Status switch
        {
            "triggered" => IncidentStatus.Triggered,
            "acknowledged" => IncidentStatus.Acknowledged,
            "resolved" => IncidentStatus.Resolved,
            _ => IncidentStatus.Triggered
        };

        var urgency = incident.Urgency switch
        {
            "high" => IncidentUrgency.High,
            "low" => IncidentUrgency.Low,
            _ => IncidentUrgency.High
        };

        var assignments = (incident.Assignments ?? [])
            .Select(a => new IncidentAssignment(
                a.Assignee?.Id ?? "",
                a.Assignee?.Summary ?? "",
                a.Assignee?.Email,
                a.At ?? DateTimeOffset.MinValue))
            .ToList();

        var timeToAck = incident.AcknowledgedAt != null && incident.CreatedAt != null
            ? incident.AcknowledgedAt - incident.CreatedAt
            : null;

        var timeToResolve = incident.ResolvedAt != null && incident.CreatedAt != null
            ? incident.ResolvedAt - incident.CreatedAt
            : null;

        return new Incident(
            incident.Id ?? "",
            incident.Title ?? "",
            incident.Description,
            status,
            urgency,
            IncidentSeverity.Unknown,
            incident.Service?.Id,
            incident.Service?.Summary,
            assignments,
            incident.CreatedBy != null ? new IncidentUser(
                incident.CreatedBy.Id ?? "",
                incident.CreatedBy.Summary ?? "",
                null) : null,
            incident.EscalationPolicy?.Id,
            incident.HtmlUrl,
            incident.CreatedAt ?? DateTimeOffset.MinValue,
            incident.AcknowledgedAt,
            incident.ResolvedAt,
            timeToAck,
            timeToResolve,
            incident.AlertCounts?.All ?? 0);
    }

    private static OnCallSchedule MapSchedule(PagerDutySchedule schedule)
    {
        return new OnCallSchedule(
            schedule.Id ?? "",
            schedule.Name ?? "",
            schedule.Description,
            schedule.TimeZone ?? "UTC",
            (schedule.ScheduleLayers ?? []).Select(l => new OnCallLayer(
                l.Id ?? "",
                l.Name ?? "",
                l.Start ?? DateTimeOffset.MinValue,
                l.RotationTurnLengthSeconds / 86400, // Convert seconds to days
                (l.Users ?? []).Select(u => u.User?.Id ?? "").ToList()
            )).ToList());
    }

    private static IncidentService MapService(PagerDutyService service)
    {
        return new IncidentService(
            service.Id ?? "",
            service.Name ?? "",
            service.Description,
            service.Status ?? "active",
            service.EscalationPolicy?.Id,
            service.CreatedAt ?? DateTimeOffset.MinValue);
    }
}

// ========================================================================
// PagerDuty Severity
// ========================================================================

/// <summary>
/// PagerDuty event severity.
/// </summary>
public enum PagerDutySeverity
{
    Critical,
    Error,
    Warning,
    Info
}

// ========================================================================
// PagerDuty DTOs
// ========================================================================

internal sealed class PagerDutyAbilitiesResponse
{
    [JsonPropertyName("abilities")]
    public List<string>? Abilities { get; set; }
}

internal sealed class PagerDutyUserResponse
{
    [JsonPropertyName("user")]
    public PagerDutyUser? User { get; set; }
}

internal sealed class PagerDutyUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}

internal sealed class PagerDutyIncidentsResponse
{
    [JsonPropertyName("incidents")]
    public List<PagerDutyIncident>? Incidents { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("more")]
    public bool More { get; set; }
}

internal sealed class PagerDutyIncidentResponse
{
    [JsonPropertyName("incident")]
    public PagerDutyIncident? Incident { get; set; }
}

internal sealed class PagerDutyIncident
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("urgency")]
    public string? Urgency { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("acknowledged_at")]
    public DateTimeOffset? AcknowledgedAt { get; set; }

    [JsonPropertyName("resolved_at")]
    public DateTimeOffset? ResolvedAt { get; set; }

    [JsonPropertyName("service")]
    public PagerDutyReference? Service { get; set; }

    [JsonPropertyName("escalation_policy")]
    public PagerDutyReference? EscalationPolicy { get; set; }

    [JsonPropertyName("created_by")]
    public PagerDutyReference? CreatedBy { get; set; }

    [JsonPropertyName("assignments")]
    public List<PagerDutyAssignment>? Assignments { get; set; }

    [JsonPropertyName("alert_counts")]
    public PagerDutyAlertCounts? AlertCounts { get; set; }
}

internal sealed class PagerDutyReference
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

internal sealed class PagerDutyAssignment
{
    [JsonPropertyName("assignee")]
    public PagerDutyReference? Assignee { get; set; }

    [JsonPropertyName("at")]
    public DateTimeOffset? At { get; set; }
}

internal sealed class PagerDutyAlertCounts
{
    [JsonPropertyName("all")]
    public int All { get; set; }

    [JsonPropertyName("triggered")]
    public int Triggered { get; set; }

    [JsonPropertyName("resolved")]
    public int Resolved { get; set; }
}

internal sealed class PagerDutySchedulesResponse
{
    [JsonPropertyName("schedules")]
    public List<PagerDutySchedule>? Schedules { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

internal sealed class PagerDutySchedule
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("time_zone")]
    public string? TimeZone { get; set; }

    [JsonPropertyName("schedule_layers")]
    public List<PagerDutyScheduleLayer>? ScheduleLayers { get; set; }
}

internal sealed class PagerDutyScheduleLayer
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; set; }

    [JsonPropertyName("rotation_turn_length_seconds")]
    public int RotationTurnLengthSeconds { get; set; }

    [JsonPropertyName("users")]
    public List<PagerDutyLayerUser>? Users { get; set; }
}

internal sealed class PagerDutyLayerUser
{
    [JsonPropertyName("user")]
    public PagerDutyReference? User { get; set; }
}

internal sealed class PagerDutyScheduleUsersResponse
{
    [JsonPropertyName("users")]
    public List<PagerDutyUser>? Users { get; set; }
}

internal sealed class PagerDutyServicesResponse
{
    [JsonPropertyName("services")]
    public List<PagerDutyService>? Services { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

internal sealed class PagerDutyService
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("escalation_policy")]
    public PagerDutyReference? EscalationPolicy { get; set; }
}

internal sealed class PagerDutyNoteResponse
{
    [JsonPropertyName("note")]
    public PagerDutyNote? Note { get; set; }
}

/// <summary>
/// PagerDuty incident note.
/// </summary>
public sealed class PagerDutyNote
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }
}

internal sealed class PagerDutyEscalationPoliciesResponse
{
    [JsonPropertyName("escalation_policies")]
    public List<PagerDutyEscalationPolicy>? EscalationPolicies { get; set; }
}

/// <summary>
/// PagerDuty escalation policy.
/// </summary>
public sealed class PagerDutyEscalationPolicy
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("num_loops")]
    public int NumLoops { get; set; }

    [JsonPropertyName("on_call_handoff_notifications")]
    public string? OnCallHandoffNotifications { get; set; }
}

internal sealed class PagerDutyEventResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("dedup_key")]
    public string? DedupKey { get; set; }
}
