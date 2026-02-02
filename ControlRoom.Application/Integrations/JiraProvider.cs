using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlRoom.Application.Integrations;

/// <summary>
/// Jira integration provider.
/// Supports projects, issues, sprints, and workflows.
/// </summary>
public sealed class JiraProvider : IIssueTrackingProvider
{
    private readonly HttpClient _httpClient;
    private string? _baseUrl;
    private string? _email;
    private string? _apiToken;

    public string ProviderName => "jira";
    public DevOpsCategory Category => DevOpsCategory.IssueTracking;

    public JiraProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Configure with Jira Cloud credentials.
    /// </summary>
    public void Configure(string baseUrl, string email, string apiToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _email = email;
        _apiToken = apiToken;
    }

    public async Task<DevOpsValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.TryGetValue("base_url", out var baseUrl) ||
            !configuration.TryGetValue("email", out var email) ||
            !configuration.TryGetValue("api_token", out var apiToken))
        {
            return new DevOpsValidationResult(
                false, "Missing required credentials: base_url, email, api_token",
                null, null, null, null, []);
        }

        Configure(baseUrl, email, apiToken);

        try
        {
            var response = await MakeRequestAsync(HttpMethod.Get, "/rest/api/3/myself", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DevOpsValidationResult(
                    false, $"Authentication failed: {response.StatusCode}",
                    null, null, null, null, []);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var user = JsonSerializer.Deserialize<JiraUser>(content);

            return new DevOpsValidationResult(
                true,
                null,
                user?.AccountId,
                user?.DisplayName,
                null,
                null,
                ["read", "write"],
                new() { ["email"] = user?.EmailAddress ?? "" });
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
            var response = await MakeRequestAsync(HttpMethod.Get, "/rest/api/3/serverInfo", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DevOpsResourceList<Project>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            "/rest/api/3/project?expand=description",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var projects = JsonSerializer.Deserialize<List<JiraProject>>(content) ?? [];

        return new DevOpsResourceList<Project>(
            projects.Select(MapProject).ToList(),
            projects.Count,
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<Project?> GetProjectAsync(
        string projectKey,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/rest/api/3/project/{projectKey}",
            cancellationToken);

        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var project = JsonSerializer.Deserialize<JiraProject>(content);

        return project != null ? MapProject(project) : null;
    }

    public async Task<DevOpsResourceList<Issue>> ListIssuesAsync(
        string projectKey,
        IssueQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var jql = BuildJql(projectKey, query);
        var maxResults = query?.MaxResults ?? 50;

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/rest/api/3/search?jql={Uri.EscapeDataString(jql)}&maxResults={maxResults}&expand=names",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var searchResult = JsonSerializer.Deserialize<JiraSearchResult>(content);

        return new DevOpsResourceList<Issue>(
            (searchResult?.Issues ?? []).Select(MapIssue).ToList(),
            searchResult?.Total ?? 0,
            searchResult?.StartAt + searchResult?.MaxResults < searchResult?.Total ? "next" : null,
            DateTimeOffset.UtcNow);
    }

    public async Task<Issue?> GetIssueAsync(
        string issueKey,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/rest/api/3/issue/{issueKey}?expand=names,renderedFields",
            cancellationToken);

        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var issue = JsonSerializer.Deserialize<JiraIssue>(content);

        return issue != null ? MapIssue(issue) : null;
    }

    public async Task<Issue> CreateIssueAsync(
        CreateIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var issueTypeId = await GetIssueTypeIdAsync(request.ProjectKey, request.Type, cancellationToken);

        var body = new
        {
            fields = new Dictionary<string, object>
            {
                ["project"] = new { key = request.ProjectKey },
                ["summary"] = request.Title,
                ["description"] = request.Description != null ? CreateAdfDocument(request.Description) : null!,
                ["issuetype"] = new { id = issueTypeId },
                ["priority"] = new { name = MapPriorityToJira(request.Priority) }
            }
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            "/rest/api/3/issue",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var created = JsonSerializer.Deserialize<JiraCreatedIssue>(content);

        return await GetIssueAsync(created?.Key ?? "", cancellationToken)
            ?? throw new InvalidOperationException("Failed to retrieve created issue");
    }

    public async Task<Issue> UpdateIssueAsync(
        string issueKey,
        UpdateIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var fields = new Dictionary<string, object>();

        if (request.Title != null)
            fields["summary"] = request.Title;

        if (request.Description != null)
            fields["description"] = CreateAdfDocument(request.Description);

        if (request.Priority != null)
            fields["priority"] = new { name = MapPriorityToJira(request.Priority.Value) };

        var body = new { fields };

        var response = await MakeRequestAsync(
            HttpMethod.Put,
            $"/rest/api/3/issue/{issueKey}",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        return await GetIssueAsync(issueKey, cancellationToken)
            ?? throw new InvalidOperationException("Failed to retrieve updated issue");
    }

    public async Task<IssueComment> AddCommentAsync(
        string issueKey,
        string body,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var requestBody = new
        {
            body = CreateAdfDocument(body)
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"/rest/api/3/issue/{issueKey}/comment",
            cancellationToken,
            JsonSerializer.Serialize(requestBody));
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var comment = JsonSerializer.Deserialize<JiraComment>(content);

        return new IssueComment(
            comment?.Id ?? "",
            body,
            new IssueUser(
                comment?.Author?.AccountId ?? "",
                comment?.Author?.DisplayName ?? "",
                comment?.Author?.EmailAddress,
                comment?.Author?.AvatarUrls?.X48),
            comment?.Created ?? DateTimeOffset.UtcNow,
            comment?.Updated);
    }

    public async Task TransitionIssueAsync(
        string issueKey,
        string transitionId,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new
        {
            transition = new { id = transitionId }
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"/rest/api/3/issue/{issueKey}/transitions",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();
    }

    // ========================================================================
    // Jira-Specific Operations
    // ========================================================================

    /// <summary>
    /// Gets available transitions for an issue.
    /// </summary>
    public async Task<IReadOnlyList<JiraTransition>> GetTransitionsAsync(
        string issueKey,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/rest/api/3/issue/{issueKey}/transitions",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<JiraTransitionsResponse>(content);

        return result?.Transitions ?? [];
    }

    /// <summary>
    /// Lists sprints in a board.
    /// </summary>
    public async Task<IReadOnlyList<JiraSprint>> ListSprintsAsync(
        int boardId,
        string? state = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var url = $"/rest/agile/1.0/board/{boardId}/sprint";
        if (state != null)
            url += $"?state={state}";

        var response = await MakeRequestAsync(HttpMethod.Get, url, cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<JiraSprintsResponse>(content);

        return result?.Values ?? [];
    }

    /// <summary>
    /// Assigns an issue to a user.
    /// </summary>
    public async Task AssignIssueAsync(
        string issueKey,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new { accountId };

        var response = await MakeRequestAsync(
            HttpMethod.Put,
            $"/rest/api/3/issue/{issueKey}/assignee",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Searches users.
    /// </summary>
    public async Task<IReadOnlyList<JiraUser>> SearchUsersAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/rest/api/3/user/search?query={Uri.EscapeDataString(query)}",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<JiraUser>>(content) ?? [];
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_email) || string.IsNullOrEmpty(_apiToken))
        {
            throw new InvalidOperationException("Jira credentials not configured. Call Configure() first.");
        }
    }

    private async Task<HttpResponseMessage> MakeRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken,
        string? body = null)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}{path}");

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_email}:{_apiToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static string BuildJql(string projectKey, IssueQuery? query)
    {
        var conditions = new List<string> { $"project = {projectKey}" };

        if (query?.StatusCategory != null)
        {
            var category = query.StatusCategory switch
            {
                IssueStatusCategory.ToDo => "To Do",
                IssueStatusCategory.InProgress => "In Progress",
                IssueStatusCategory.Done => "Done",
                _ => null
            };
            if (category != null)
                conditions.Add($"statusCategory = \"{category}\"");
        }

        if (query?.Type != null)
        {
            var typeName = query.Type switch
            {
                IssueType.Bug => "Bug",
                IssueType.Task => "Task",
                IssueType.Story => "Story",
                IssueType.Epic => "Epic",
                _ => null
            };
            if (typeName != null)
                conditions.Add($"issuetype = {typeName}");
        }

        if (query?.AssigneeId != null)
            conditions.Add($"assignee = \"{query.AssigneeId}\"");

        if (query?.SprintId != null)
            conditions.Add($"sprint = {query.SprintId}");

        if (query?.SearchText != null)
            conditions.Add($"text ~ \"{query.SearchText}\"");

        var jql = string.Join(" AND ", conditions);

        if (query?.OrderBy != null)
            jql += $" ORDER BY {query.OrderBy}";
        else
            jql += " ORDER BY updated DESC";

        return jql;
    }

    private async Task<string> GetIssueTypeIdAsync(string projectKey, IssueType type, CancellationToken cancellationToken)
    {
        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/rest/api/3/project/{projectKey}",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var project = JsonSerializer.Deserialize<JiraProject>(content);

        var typeName = type switch
        {
            IssueType.Bug => "Bug",
            IssueType.Task => "Task",
            IssueType.Story => "Story",
            IssueType.Epic => "Epic",
            _ => "Task"
        };

        var issueType = project?.IssueTypes?.FirstOrDefault(t =>
            t.Name?.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true);

        return issueType?.Id ?? throw new InvalidOperationException($"Issue type {typeName} not found in project");
    }

    private static object CreateAdfDocument(string text)
    {
        return new
        {
            type = "doc",
            version = 1,
            content = new[]
            {
                new
                {
                    type = "paragraph",
                    content = new[]
                    {
                        new { type = "text", text }
                    }
                }
            }
        };
    }

    private static string MapPriorityToJira(IssuePriority priority) => priority switch
    {
        IssuePriority.Highest or IssuePriority.Critical => "Highest",
        IssuePriority.High or IssuePriority.Urgent => "High",
        IssuePriority.Medium => "Medium",
        IssuePriority.Low => "Low",
        IssuePriority.Lowest => "Lowest",
        _ => "Medium"
    };

    private static Project MapProject(JiraProject project)
    {
        return new Project(
            project.Id ?? "",
            project.Key ?? "",
            project.Name ?? "",
            project.Description,
            project.Self,
            project.AvatarUrls?.X48,
            project.Lead != null ? new ProjectOwner(
                project.Lead.AccountId ?? "",
                project.Lead.DisplayName ?? "",
                project.Lead.EmailAddress,
                project.Lead.AvatarUrls?.X48) : null,
            DateTimeOffset.MinValue,
            new() { ["style"] = project.Style ?? "" });
    }

    private static Issue MapIssue(JiraIssue issue)
    {
        var fields = issue.Fields;

        return new Issue(
            issue.Id ?? "",
            issue.Key ?? "",
            fields?.Summary ?? "",
            ExtractPlainText(fields?.Description),
            MapIssueType(fields?.IssueType?.Name),
            MapPriority(fields?.Priority?.Name),
            new IssueStatus(
                fields?.Status?.Id ?? "",
                fields?.Status?.Name ?? "",
                MapStatusCategory(fields?.Status?.StatusCategory?.Name)),
            fields?.Project?.Key ?? "",
            fields?.Assignee != null ? new IssueUser(
                fields.Assignee.AccountId ?? "",
                fields.Assignee.DisplayName ?? "",
                fields.Assignee.EmailAddress,
                fields.Assignee.AvatarUrls?.X48) : null,
            new IssueUser(
                fields?.Reporter?.AccountId ?? "",
                fields?.Reporter?.DisplayName ?? "",
                fields?.Reporter?.EmailAddress,
                fields?.Reporter?.AvatarUrls?.X48),
            fields?.Labels ?? [],
            null,
            null,
            null,
            fields?.Created ?? DateTimeOffset.MinValue,
            fields?.Updated ?? DateTimeOffset.MinValue,
            fields?.DueDate,
            fields?.ResolutionDate,
            $"{issue.Self?.Split("/rest/")[0]}/browse/{issue.Key}");
    }

    private static string? ExtractPlainText(object? description)
    {
        if (description == null) return null;
        if (description is string s) return s;

        // Try to extract text from ADF format
        try
        {
            var json = JsonSerializer.Serialize(description);
            // Simple extraction - in real implementation would properly parse ADF
            return json;
        }
        catch
        {
            return description.ToString();
        }
    }

    private static IssueType MapIssueType(string? typeName) => typeName?.ToLowerInvariant() switch
    {
        "bug" => IssueType.Bug,
        "task" => IssueType.Task,
        "story" => IssueType.Story,
        "epic" => IssueType.Epic,
        "sub-task" or "subtask" => IssueType.Subtask,
        _ => IssueType.Other
    };

    private static IssuePriority MapPriority(string? priorityName) => priorityName?.ToLowerInvariant() switch
    {
        "highest" => IssuePriority.Highest,
        "high" => IssuePriority.High,
        "medium" => IssuePriority.Medium,
        "low" => IssuePriority.Low,
        "lowest" => IssuePriority.Lowest,
        _ => IssuePriority.None
    };

    private static IssueStatusCategory MapStatusCategory(string? categoryName) => categoryName?.ToLowerInvariant() switch
    {
        "to do" or "new" => IssueStatusCategory.ToDo,
        "in progress" => IssueStatusCategory.InProgress,
        "done" or "complete" => IssueStatusCategory.Done,
        _ => IssueStatusCategory.ToDo
    };
}

// ========================================================================
// Jira DTOs
// ========================================================================

/// <summary>
/// Jira user.
/// </summary>
public sealed class JiraUser
{
    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }

    [JsonPropertyName("avatarUrls")]
    public JiraAvatarUrls? AvatarUrls { get; set; }
}

/// <summary>
/// Jira avatar URLs.
/// </summary>
public sealed class JiraAvatarUrls
{
    [JsonPropertyName("48x48")]
    public string? X48 { get; set; }

    [JsonPropertyName("24x24")]
    public string? X24 { get; set; }
}

internal sealed class JiraProject
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("self")]
    public string? Self { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("avatarUrls")]
    public JiraAvatarUrls? AvatarUrls { get; set; }

    [JsonPropertyName("lead")]
    public JiraUser? Lead { get; set; }

    [JsonPropertyName("issueTypes")]
    public List<JiraIssueType>? IssueTypes { get; set; }
}

internal sealed class JiraIssueType
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class JiraSearchResult
{
    [JsonPropertyName("startAt")]
    public int StartAt { get; set; }

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("issues")]
    public List<JiraIssue>? Issues { get; set; }
}

internal sealed class JiraIssue
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("self")]
    public string? Self { get; set; }

    [JsonPropertyName("fields")]
    public JiraIssueFields? Fields { get; set; }
}

internal sealed class JiraIssueFields
{
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("description")]
    public object? Description { get; set; }

    [JsonPropertyName("issuetype")]
    public JiraIssueType? IssueType { get; set; }

    [JsonPropertyName("priority")]
    public JiraPriority? Priority { get; set; }

    [JsonPropertyName("status")]
    public JiraStatus? Status { get; set; }

    [JsonPropertyName("project")]
    public JiraProjectRef? Project { get; set; }

    [JsonPropertyName("assignee")]
    public JiraUser? Assignee { get; set; }

    [JsonPropertyName("reporter")]
    public JiraUser? Reporter { get; set; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; set; }

    [JsonPropertyName("duedate")]
    public DateTimeOffset? DueDate { get; set; }

    [JsonPropertyName("resolutiondate")]
    public DateTimeOffset? ResolutionDate { get; set; }
}

internal sealed class JiraProjectRef
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }
}

internal sealed class JiraPriority
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// Jira issue status.
/// </summary>
public sealed class JiraStatus
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("statusCategory")]
    public JiraStatusCategory? StatusCategory { get; set; }
}

/// <summary>
/// Jira status category.
/// </summary>
public sealed class JiraStatusCategory
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class JiraCreatedIssue
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }
}

internal sealed class JiraComment
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("author")]
    public JiraUser? Author { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; set; }
}

internal sealed class JiraTransitionsResponse
{
    [JsonPropertyName("transitions")]
    public List<JiraTransition>? Transitions { get; set; }
}

/// <summary>
/// Jira issue transition.
/// </summary>
public sealed class JiraTransition
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("to")]
    public JiraStatus? To { get; set; }
}

internal sealed class JiraSprintsResponse
{
    [JsonPropertyName("values")]
    public List<JiraSprint>? Values { get; set; }
}

/// <summary>
/// Jira sprint.
/// </summary>
public sealed class JiraSprint
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("startDate")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("goal")]
    public string? Goal { get; set; }
}
