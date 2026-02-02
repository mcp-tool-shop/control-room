using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlRoom.Application.Integrations;

/// <summary>
/// GitHub integration provider.
/// Supports repositories, pull requests, issues, actions, and webhooks.
/// </summary>
public sealed class GitHubProvider : ISourceControlProvider
{
    private readonly HttpClient _httpClient;
    private string? _token;
    private string? _baseUrl;

    public string ProviderName => "github";
    public DevOpsCategory Category => DevOpsCategory.SourceControl;

    private const string DefaultBaseUrl = "https://api.github.com";

    public GitHubProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Configure with personal access token or OAuth token.
    /// </summary>
    public void Configure(string token, string? baseUrl = null)
    {
        _token = token;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
    }

    public async Task<DevOpsValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.TryGetValue("token", out var token))
        {
            return new DevOpsValidationResult(
                false, "Missing required token", null, null, null, null, []);
        }

        Configure(token, configuration.GetValueOrDefault("base_url"));

        try
        {
            var response = await MakeRequestAsync(HttpMethod.Get, "/user", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DevOpsValidationResult(
                    false, $"Authentication failed: {response.StatusCode}",
                    null, null, null, null, []);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var user = JsonSerializer.Deserialize<GitHubUser>(content);

            // Get scopes from response headers
            var scopes = response.Headers.TryGetValues("X-OAuth-Scopes", out var scopeValues)
                ? scopeValues.FirstOrDefault()?.Split(',').Select(s => s.Trim()).ToList() ?? []
                : new List<string>();

            return new DevOpsValidationResult(
                true,
                null,
                user?.Id.ToString(),
                user?.Login,
                null,
                null,
                scopes,
                new() { ["name"] = user?.Name ?? "", ["email"] = user?.Email ?? "" });
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
            var response = await MakeRequestAsync(HttpMethod.Get, "/", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DevOpsResourceList<Repository>> ListRepositoriesAsync(
        string? organization = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var path = organization != null ? $"/orgs/{organization}/repos" : "/user/repos";
        var response = await MakeRequestAsync(HttpMethod.Get, $"{path}?per_page=100", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var repos = JsonSerializer.Deserialize<List<GitHubRepository>>(content) ?? [];

        return new DevOpsResourceList<Repository>(
            repos.Select(MapRepository).ToList(),
            repos.Count,
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<Repository?> GetRepositoryAsync(
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(HttpMethod.Get, $"/repos/{owner}/{name}", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var repo = JsonSerializer.Deserialize<GitHubRepository>(content);

        return repo != null ? MapRepository(repo) : null;
    }

    public async Task<DevOpsResourceList<PullRequest>> ListPullRequestsAsync(
        string owner,
        string repo,
        PullRequestState? state = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var stateParam = state switch
        {
            PullRequestState.Open => "open",
            PullRequestState.Closed => "closed",
            PullRequestState.Merged => "closed",
            PullRequestState.All => "all",
            _ => "open"
        };

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/repos/{owner}/{repo}/pulls?state={stateParam}&per_page=100",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var prs = JsonSerializer.Deserialize<List<GitHubPullRequest>>(content) ?? [];

        var result = prs
            .Where(pr => state != PullRequestState.Merged || pr.MergedAt != null)
            .Select(MapPullRequest)
            .ToList();

        return new DevOpsResourceList<PullRequest>(
            result,
            result.Count,
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<PullRequest?> GetPullRequestAsync(
        string owner,
        string repo,
        int number,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/repos/{owner}/{repo}/pulls/{number}",
            cancellationToken);

        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var pr = JsonSerializer.Deserialize<GitHubPullRequest>(content);

        return pr != null ? MapPullRequest(pr) : null;
    }

    public async Task<PullRequest> CreatePullRequestAsync(
        string owner,
        string repo,
        CreatePullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new
        {
            title = request.Title,
            head = request.SourceBranch,
            @base = request.TargetBranch,
            body = request.Body,
            draft = request.IsDraft
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/pulls",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var pr = JsonSerializer.Deserialize<GitHubPullRequest>(content)
            ?? throw new InvalidOperationException("Failed to parse pull request response");

        // Add labels if specified
        if (request.Labels?.Count > 0)
        {
            await MakeRequestAsync(
                HttpMethod.Post,
                $"/repos/{owner}/{repo}/issues/{pr.Number}/labels",
                cancellationToken,
                JsonSerializer.Serialize(new { labels = request.Labels }));
        }

        // Request reviewers if specified
        if (request.Reviewers?.Count > 0)
        {
            await MakeRequestAsync(
                HttpMethod.Post,
                $"/repos/{owner}/{repo}/pulls/{pr.Number}/requested_reviewers",
                cancellationToken,
                JsonSerializer.Serialize(new { reviewers = request.Reviewers }));
        }

        return MapPullRequest(pr);
    }

    public async Task<DevOpsResourceList<Branch>> ListBranchesAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/repos/{owner}/{repo}/branches?per_page=100",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var branches = JsonSerializer.Deserialize<List<GitHubBranch>>(content) ?? [];

        // Get default branch
        var repoResponse = await MakeRequestAsync(HttpMethod.Get, $"/repos/{owner}/{repo}", cancellationToken);
        var repoContent = await repoResponse.Content.ReadAsStringAsync(cancellationToken);
        var repoInfo = JsonSerializer.Deserialize<GitHubRepository>(repoContent);
        var defaultBranch = repoInfo?.DefaultBranch ?? "main";

        return new DevOpsResourceList<Branch>(
            branches.Select(b => MapBranch(b, defaultBranch)).ToList(),
            branches.Count,
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<DevOpsResourceList<WorkflowRun>> ListWorkflowRunsAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/repos/{owner}/{repo}/actions/runs?per_page=50",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var runsResponse = JsonSerializer.Deserialize<GitHubWorkflowRunsResponse>(content);
        var runs = runsResponse?.WorkflowRuns ?? [];

        return new DevOpsResourceList<WorkflowRun>(
            runs.Select(MapWorkflowRun).ToList(),
            runsResponse?.TotalCount ?? runs.Count,
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<WorkflowRun> TriggerWorkflowAsync(
        string owner,
        string repo,
        string workflowId,
        string branch,
        Dictionary<string, string>? inputs = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new
        {
            @ref = branch,
            inputs = inputs ?? new Dictionary<string, string>()
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/actions/workflows/{workflowId}/dispatches",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        // Workflow dispatch doesn't return the run, so we need to fetch the latest
        await Task.Delay(1000, cancellationToken); // Brief delay for run to be created

        var runsResponse = await MakeRequestAsync(
            HttpMethod.Get,
            $"/repos/{owner}/{repo}/actions/workflows/{workflowId}/runs?per_page=1",
            cancellationToken);

        var runsContent = await runsResponse.Content.ReadAsStringAsync(cancellationToken);
        var runs = JsonSerializer.Deserialize<GitHubWorkflowRunsResponse>(runsContent);
        var latestRun = runs?.WorkflowRuns?.FirstOrDefault()
            ?? throw new InvalidOperationException("Workflow triggered but run not found");

        return MapWorkflowRun(latestRun);
    }

    // ========================================================================
    // GitHub-Specific Operations
    // ========================================================================

    /// <summary>
    /// Lists issues in a repository.
    /// </summary>
    public async Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(
        string owner,
        string repo,
        string state = "open",
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Get,
            $"/repos/{owner}/{repo}/issues?state={state}&per_page=100",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<GitHubIssue>>(content) ?? [];
    }

    /// <summary>
    /// Creates a GitHub issue.
    /// </summary>
    public async Task<GitHubIssue> CreateIssueAsync(
        string owner,
        string repo,
        string title,
        string? body = null,
        IReadOnlyList<string>? labels = null,
        IReadOnlyList<string>? assignees = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var requestBody = new
        {
            title,
            body,
            labels,
            assignees
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/issues",
            cancellationToken,
            JsonSerializer.Serialize(requestBody));
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<GitHubIssue>(content)
            ?? throw new InvalidOperationException("Failed to create issue");
    }

    /// <summary>
    /// Adds a comment to an issue or pull request.
    /// </summary>
    public async Task<GitHubComment> AddCommentAsync(
        string owner,
        string repo,
        int issueNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/issues/{issueNumber}/comments",
            cancellationToken,
            JsonSerializer.Serialize(new { body }));
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<GitHubComment>(content)
            ?? throw new InvalidOperationException("Failed to create comment");
    }

    /// <summary>
    /// Merges a pull request.
    /// </summary>
    public async Task<bool> MergePullRequestAsync(
        string owner,
        string repo,
        int number,
        string? commitTitle = null,
        string? commitMessage = null,
        string mergeMethod = "merge",
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new
        {
            commit_title = commitTitle,
            commit_message = commitMessage,
            merge_method = mergeMethod
        };

        var response = await MakeRequestAsync(
            HttpMethod.Put,
            $"/repos/{owner}/{repo}/pulls/{number}/merge",
            cancellationToken,
            JsonSerializer.Serialize(body));

        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Creates a webhook.
    /// </summary>
    public async Task<GitHubWebhook> CreateWebhookAsync(
        string owner,
        string repo,
        string url,
        IReadOnlyList<string> events,
        string? secret = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new
        {
            name = "web",
            active = true,
            events,
            config = new
            {
                url,
                content_type = "json",
                secret,
                insecure_ssl = "0"
            }
        };

        var response = await MakeRequestAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/hooks",
            cancellationToken,
            JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<GitHubWebhook>(content)
            ?? throw new InvalidOperationException("Failed to create webhook");
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_token))
        {
            throw new InvalidOperationException("GitHub token not configured. Call Configure() first.");
        }
    }

    private async Task<HttpResponseMessage> MakeRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken,
        string? body = null)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("ControlRoom/1.0");

        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static Repository MapRepository(GitHubRepository repo)
    {
        return new Repository(
            repo.Id.ToString(),
            repo.Name ?? "",
            repo.FullName ?? "",
            repo.Description,
            repo.DefaultBranch ?? "main",
            repo.HtmlUrl ?? "",
            repo.CloneUrl ?? "",
            repo.Private,
            repo.Fork,
            repo.Archived,
            repo.Language,
            repo.StargazersCount,
            repo.ForksCount,
            repo.OpenIssuesCount,
            repo.CreatedAt,
            repo.UpdatedAt,
            repo.PushedAt,
            new RepositoryOwner(
                repo.Owner?.Id.ToString() ?? "",
                repo.Owner?.Login ?? "",
                repo.Owner?.Type ?? "User",
                repo.Owner?.AvatarUrl));
    }

    private static PullRequest MapPullRequest(GitHubPullRequest pr)
    {
        var state = pr.MergedAt != null ? PullRequestState.Merged
            : pr.State == "closed" ? PullRequestState.Closed
            : PullRequestState.Open;

        return new PullRequest(
            pr.Number,
            pr.Id.ToString(),
            pr.Title ?? "",
            pr.Body,
            state,
            pr.Head?.Ref ?? "",
            pr.Base?.Ref ?? "",
            pr.HtmlUrl ?? "",
            pr.Draft,
            pr.Mergeable ?? false,
            pr.Merged,
            pr.MergedAt,
            new PullRequestUser(
                pr.User?.Id.ToString() ?? "",
                pr.User?.Login ?? "",
                pr.User?.Name,
                pr.User?.AvatarUrl),
            pr.RequestedReviewers?.Select(r => new PullRequestUser(
                r.Id.ToString(), r.Login ?? "", r.Name, r.AvatarUrl)).ToList() ?? [],
            pr.Labels?.Select(l => l.Name ?? "").ToList() ?? [],
            pr.Comments,
            pr.Commits,
            pr.Additions,
            pr.Deletions,
            pr.CreatedAt,
            pr.UpdatedAt);
    }

    private static Branch MapBranch(GitHubBranch branch, string defaultBranch)
    {
        return new Branch(
            branch.Name ?? "",
            branch.Commit?.Sha ?? "",
            branch.Protected,
            branch.Name == defaultBranch,
            null);
    }

    private static WorkflowRun MapWorkflowRun(GitHubWorkflowRun run)
    {
        var status = run.Status switch
        {
            "queued" => WorkflowRunStatus.Queued,
            "in_progress" => WorkflowRunStatus.InProgress,
            "completed" => WorkflowRunStatus.Completed,
            "waiting" => WorkflowRunStatus.Waiting,
            _ => WorkflowRunStatus.Pending
        };

        var conclusion = run.Conclusion switch
        {
            "success" => WorkflowRunConclusion.Success,
            "failure" => WorkflowRunConclusion.Failure,
            "cancelled" => WorkflowRunConclusion.Cancelled,
            "skipped" => WorkflowRunConclusion.Skipped,
            "timed_out" => WorkflowRunConclusion.TimedOut,
            "action_required" => WorkflowRunConclusion.ActionRequired,
            _ => (WorkflowRunConclusion?)null
        };

        var duration = run.RunStartedAt != null && run.UpdatedAt != null
            ? run.UpdatedAt - run.RunStartedAt
            : null;

        return new WorkflowRun(
            run.Id.ToString(),
            run.Name ?? "",
            run.WorkflowId.ToString(),
            status,
            conclusion,
            run.HeadBranch ?? "",
            run.HeadSha ?? "",
            run.HeadCommit?.Message,
            run.HtmlUrl ?? "",
            run.RunNumber,
            run.RunAttempt,
            run.CreatedAt,
            run.RunStartedAt,
            run.UpdatedAt,
            duration,
            run.Actor != null ? new PullRequestUser(
                run.Actor.Id.ToString(), run.Actor.Login ?? "", null, run.Actor.AvatarUrl) : null);
    }
}

// ========================================================================
// GitHub DTOs
// ========================================================================

internal sealed class GitHubUser
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

internal sealed class GitHubRepository
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("clone_url")]
    public string? CloneUrl { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("fork")]
    public bool Fork { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("stargazers_count")]
    public int StargazersCount { get; set; }

    [JsonPropertyName("forks_count")]
    public int ForksCount { get; set; }

    [JsonPropertyName("open_issues_count")]
    public int OpenIssuesCount { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("pushed_at")]
    public DateTimeOffset? PushedAt { get; set; }

    [JsonPropertyName("owner")]
    public GitHubUser? Owner { get; set; }
}

internal sealed class GitHubPullRequest
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("mergeable")]
    public bool? Mergeable { get; set; }

    [JsonPropertyName("merged")]
    public bool Merged { get; set; }

    [JsonPropertyName("merged_at")]
    public DateTimeOffset? MergedAt { get; set; }

    [JsonPropertyName("user")]
    public GitHubUser? User { get; set; }

    [JsonPropertyName("head")]
    public GitHubRef? Head { get; set; }

    [JsonPropertyName("base")]
    public GitHubRef? Base { get; set; }

    [JsonPropertyName("requested_reviewers")]
    public List<GitHubUser>? RequestedReviewers { get; set; }

    [JsonPropertyName("labels")]
    public List<GitHubLabel>? Labels { get; set; }

    [JsonPropertyName("comments")]
    public int Comments { get; set; }

    [JsonPropertyName("commits")]
    public int Commits { get; set; }

    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class GitHubRef
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

internal sealed class GitHubLabel
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

internal sealed class GitHubBranch
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("protected")]
    public bool Protected { get; set; }

    [JsonPropertyName("commit")]
    public GitHubCommitRef? Commit { get; set; }
}

internal sealed class GitHubCommitRef
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

internal sealed class GitHubWorkflowRunsResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("workflow_runs")]
    public List<GitHubWorkflowRun>? WorkflowRuns { get; set; }
}

internal sealed class GitHubWorkflowRun
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("workflow_id")]
    public long WorkflowId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("head_branch")]
    public string? HeadBranch { get; set; }

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("run_number")]
    public int RunNumber { get; set; }

    [JsonPropertyName("run_attempt")]
    public int? RunAttempt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("run_started_at")]
    public DateTimeOffset? RunStartedAt { get; set; }

    [JsonPropertyName("actor")]
    public GitHubUser? Actor { get; set; }

    [JsonPropertyName("head_commit")]
    public GitHubHeadCommit? HeadCommit { get; set; }
}

internal sealed class GitHubHeadCommit
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// GitHub issue.
/// </summary>
public sealed class GitHubIssue
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// GitHub comment.
/// </summary>
public sealed class GitHubComment
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// GitHub webhook.
/// </summary>
public sealed class GitHubWebhook
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("events")]
    public List<string>? Events { get; set; }
}
