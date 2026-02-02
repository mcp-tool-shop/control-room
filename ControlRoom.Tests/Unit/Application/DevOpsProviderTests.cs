using ControlRoom.Application.Integrations;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Tests for DevOps provider integrations (GitHub, Jira, PagerDuty, Slack).
/// </summary>
public sealed class DevOpsProviderTests
{
    // ========================================================================
    // Interface Tests
    // ========================================================================

    [Fact]
    public void IDevOpsProvider_HasRequiredMembers()
    {
        var type = typeof(IDevOpsProvider);
        Assert.Contains(type.GetProperties(), p => p.Name == "ProviderName");
        Assert.Contains(type.GetProperties(), p => p.Name == "Category");
        Assert.Contains(type.GetMethods(), m => m.Name == "ValidateCredentialsAsync");
        Assert.Contains(type.GetMethods(), m => m.Name == "TestConnectionAsync");
    }

    [Fact]
    public void ISourceControlProvider_ExtendsIDevOpsProvider()
    {
        Assert.True(typeof(IDevOpsProvider).IsAssignableFrom(typeof(ISourceControlProvider)));
    }

    [Fact]
    public void IIssueTrackingProvider_ExtendsIDevOpsProvider()
    {
        Assert.True(typeof(IDevOpsProvider).IsAssignableFrom(typeof(IIssueTrackingProvider)));
    }

    [Fact]
    public void IIncidentManagementProvider_ExtendsIDevOpsProvider()
    {
        Assert.True(typeof(IDevOpsProvider).IsAssignableFrom(typeof(IIncidentManagementProvider)));
    }

    [Fact]
    public void ICommunicationProvider_ExtendsIDevOpsProvider()
    {
        Assert.True(typeof(IDevOpsProvider).IsAssignableFrom(typeof(ICommunicationProvider)));
    }

    // ========================================================================
    // DevOpsValidationResult Tests
    // ========================================================================

    [Fact]
    public void DevOpsValidationResult_Valid_HasAllProperties()
    {
        var result = new DevOpsValidationResult(
            true,
            null,
            "user-123",
            "John Doe",
            "org-456",
            "Acme Corp",
            ["read", "write", "admin"],
            new() { ["email"] = "john@example.com" });

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("user-123", result.UserId);
        Assert.Equal("John Doe", result.UserName);
        Assert.Equal("org-456", result.OrganizationId);
        Assert.Equal("Acme Corp", result.OrganizationName);
        Assert.Equal(3, result.Scopes.Count);
    }

    [Fact]
    public void DevOpsValidationResult_Invalid_HasErrorMessage()
    {
        var result = new DevOpsValidationResult(
            false,
            "Invalid token",
            null, null, null, null, []);

        Assert.False(result.IsValid);
        Assert.Equal("Invalid token", result.ErrorMessage);
    }

    // ========================================================================
    // DevOpsCategory Tests
    // ========================================================================

    [Theory]
    [InlineData(DevOpsCategory.SourceControl)]
    [InlineData(DevOpsCategory.IssueTracking)]
    [InlineData(DevOpsCategory.IncidentManagement)]
    [InlineData(DevOpsCategory.Communication)]
    [InlineData(DevOpsCategory.CiCd)]
    [InlineData(DevOpsCategory.Monitoring)]
    public void DevOpsCategory_HasExpectedValues(DevOpsCategory category)
    {
        Assert.True(Enum.IsDefined(typeof(DevOpsCategory), category));
    }

    // ========================================================================
    // GitHub Provider Tests
    // ========================================================================

    [Fact]
    public void GitHubProvider_ProviderName_IsCorrect()
    {
        var provider = new GitHubProvider();
        Assert.Equal("github", provider.ProviderName);
        Assert.Equal(DevOpsCategory.SourceControl, provider.Category);
    }

    [Fact]
    public async Task GitHubProvider_ValidateCredentials_FailsWithoutToken()
    {
        var provider = new GitHubProvider();
        var result = await provider.ValidateCredentialsAsync(new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains("Missing required token", result.ErrorMessage);
    }

    // ========================================================================
    // Repository Tests
    // ========================================================================

    [Fact]
    public void Repository_HasAllProperties()
    {
        var owner = new RepositoryOwner("1", "octocat", "User", "https://avatars.com/1");
        var repo = new Repository(
            "12345",
            "hello-world",
            "octocat/hello-world",
            "A sample repository",
            "main",
            "https://github.com/octocat/hello-world",
            "https://github.com/octocat/hello-world.git",
            false,
            false,
            false,
            "C#",
            100,
            25,
            5,
            DateTimeOffset.UtcNow.AddYears(-1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(-1),
            owner);

        Assert.Equal("12345", repo.Id);
        Assert.Equal("hello-world", repo.Name);
        Assert.Equal("octocat/hello-world", repo.FullName);
        Assert.Equal("main", repo.DefaultBranch);
        Assert.False(repo.IsPrivate);
        Assert.Equal(100, repo.StarCount);
        Assert.Equal("octocat", repo.Owner.Login);
    }

    // ========================================================================
    // Pull Request Tests
    // ========================================================================

    [Fact]
    public void PullRequest_HasAllProperties()
    {
        var author = new PullRequestUser("1", "developer", "Dev User", null);
        var pr = new PullRequest(
            42,
            "pr-123",
            "Add new feature",
            "This PR adds a new feature",
            PullRequestState.Open,
            "feature-branch",
            "main",
            "https://github.com/org/repo/pull/42",
            false,
            true,
            false,
            null,
            author,
            [new PullRequestUser("2", "reviewer", null, null)],
            ["enhancement"],
            5,
            3,
            100,
            20,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow);

        Assert.Equal(42, pr.Number);
        Assert.Equal("Add new feature", pr.Title);
        Assert.Equal(PullRequestState.Open, pr.State);
        Assert.Equal("feature-branch", pr.SourceBranch);
        Assert.Equal("main", pr.TargetBranch);
        Assert.True(pr.IsMergeable);
        Assert.False(pr.IsMerged);
        Assert.Single(pr.Reviewers);
    }

    [Theory]
    [InlineData(PullRequestState.Open)]
    [InlineData(PullRequestState.Closed)]
    [InlineData(PullRequestState.Merged)]
    [InlineData(PullRequestState.All)]
    public void PullRequestState_HasExpectedValues(PullRequestState state)
    {
        Assert.True(Enum.IsDefined(typeof(PullRequestState), state));
    }

    // ========================================================================
    // Workflow Run Tests
    // ========================================================================

    [Fact]
    public void WorkflowRun_HasAllProperties()
    {
        var run = new WorkflowRun(
            "run-123",
            "CI Build",
            "workflow-456",
            WorkflowRunStatus.Completed,
            WorkflowRunConclusion.Success,
            "main",
            "abc123",
            "Fix tests",
            "https://github.com/org/repo/actions/runs/123",
            42,
            1,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow.AddMinutes(-9),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(9),
            new PullRequestUser("1", "dev", null, null));

        Assert.Equal("run-123", run.Id);
        Assert.Equal("CI Build", run.Name);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.Equal(WorkflowRunConclusion.Success, run.Conclusion);
        Assert.Equal(42, run.RunNumber);
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Queued)]
    [InlineData(WorkflowRunStatus.InProgress)]
    [InlineData(WorkflowRunStatus.Completed)]
    [InlineData(WorkflowRunStatus.Waiting)]
    [InlineData(WorkflowRunStatus.Pending)]
    public void WorkflowRunStatus_HasExpectedValues(WorkflowRunStatus status)
    {
        Assert.True(Enum.IsDefined(typeof(WorkflowRunStatus), status));
    }

    [Theory]
    [InlineData(WorkflowRunConclusion.Success)]
    [InlineData(WorkflowRunConclusion.Failure)]
    [InlineData(WorkflowRunConclusion.Cancelled)]
    [InlineData(WorkflowRunConclusion.Skipped)]
    [InlineData(WorkflowRunConclusion.TimedOut)]
    public void WorkflowRunConclusion_HasExpectedValues(WorkflowRunConclusion conclusion)
    {
        Assert.True(Enum.IsDefined(typeof(WorkflowRunConclusion), conclusion));
    }

    // ========================================================================
    // Jira Provider Tests
    // ========================================================================

    [Fact]
    public void JiraProvider_ProviderName_IsCorrect()
    {
        var provider = new JiraProvider();
        Assert.Equal("jira", provider.ProviderName);
        Assert.Equal(DevOpsCategory.IssueTracking, provider.Category);
    }

    [Fact]
    public async Task JiraProvider_ValidateCredentials_FailsWithoutCredentials()
    {
        var provider = new JiraProvider();
        var result = await provider.ValidateCredentialsAsync(new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains("Missing required credentials", result.ErrorMessage);
    }

    // ========================================================================
    // Issue Tests
    // ========================================================================

    [Fact]
    public void Issue_HasAllProperties()
    {
        var assignee = new IssueUser("user-1", "John Doe", "john@example.com", null);
        var reporter = new IssueUser("user-2", "Jane Doe", "jane@example.com", null);
        var status = new IssueStatus("status-1", "In Progress", IssueStatusCategory.InProgress);

        var issue = new Issue(
            "issue-123",
            "PROJ-42",
            "Fix login bug",
            "The login button doesn't work on mobile",
            IssueType.Bug,
            IssuePriority.High,
            status,
            "PROJ",
            assignee,
            reporter,
            ["bug", "urgent"],
            5,
            null,
            null,
            DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7),
            null,
            "https://jira.example.com/browse/PROJ-42");

        Assert.Equal("PROJ-42", issue.Key);
        Assert.Equal("Fix login bug", issue.Title);
        Assert.Equal(IssueType.Bug, issue.Type);
        Assert.Equal(IssuePriority.High, issue.Priority);
        Assert.Equal(IssueStatusCategory.InProgress, issue.Status.Category);
        Assert.Equal("John Doe", issue.Assignee?.Name);
    }

    [Theory]
    [InlineData(IssueType.Bug)]
    [InlineData(IssueType.Task)]
    [InlineData(IssueType.Story)]
    [InlineData(IssueType.Epic)]
    [InlineData(IssueType.Subtask)]
    [InlineData(IssueType.Feature)]
    public void IssueType_HasExpectedValues(IssueType type)
    {
        Assert.True(Enum.IsDefined(typeof(IssueType), type));
    }

    [Theory]
    [InlineData(IssuePriority.Lowest)]
    [InlineData(IssuePriority.Low)]
    [InlineData(IssuePriority.Medium)]
    [InlineData(IssuePriority.High)]
    [InlineData(IssuePriority.Highest)]
    [InlineData(IssuePriority.Critical)]
    public void IssuePriority_HasExpectedValues(IssuePriority priority)
    {
        Assert.True(Enum.IsDefined(typeof(IssuePriority), priority));
    }

    [Theory]
    [InlineData(IssueStatusCategory.ToDo)]
    [InlineData(IssueStatusCategory.InProgress)]
    [InlineData(IssueStatusCategory.Done)]
    [InlineData(IssueStatusCategory.Cancelled)]
    public void IssueStatusCategory_HasExpectedValues(IssueStatusCategory category)
    {
        Assert.True(Enum.IsDefined(typeof(IssueStatusCategory), category));
    }

    // ========================================================================
    // PagerDuty Provider Tests
    // ========================================================================

    [Fact]
    public void PagerDutyProvider_ProviderName_IsCorrect()
    {
        var provider = new PagerDutyProvider();
        Assert.Equal("pagerduty", provider.ProviderName);
        Assert.Equal(DevOpsCategory.IncidentManagement, provider.Category);
    }

    [Fact]
    public async Task PagerDutyProvider_ValidateCredentials_FailsWithoutApiKey()
    {
        var provider = new PagerDutyProvider();
        var result = await provider.ValidateCredentialsAsync(new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains("Missing required api_key", result.ErrorMessage);
    }

    // ========================================================================
    // Incident Tests
    // ========================================================================

    [Fact]
    public void Incident_HasAllProperties()
    {
        var assignments = new List<IncidentAssignment>
        {
            new("user-1", "On-Call Engineer", "oncall@example.com", DateTimeOffset.UtcNow)
        };

        var incident = new Incident(
            "incident-123",
            "Database connection timeout",
            "Multiple services reporting connection issues",
            IncidentStatus.Triggered,
            IncidentUrgency.High,
            IncidentSeverity.Sev2,
            "service-456",
            "Production Database",
            assignments,
            new IncidentUser("user-2", "Monitoring System", null),
            "policy-789",
            "https://pagerduty.com/incidents/123",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            null,
            null,
            null,
            null,
            3);

        Assert.Equal("incident-123", incident.Id);
        Assert.Equal("Database connection timeout", incident.Title);
        Assert.Equal(IncidentStatus.Triggered, incident.Status);
        Assert.Equal(IncidentUrgency.High, incident.Urgency);
        Assert.Equal(IncidentSeverity.Sev2, incident.Severity);
        Assert.Single(incident.Assignments);
        Assert.Equal(3, incident.AlertCount);
    }

    [Theory]
    [InlineData(IncidentStatus.Triggered)]
    [InlineData(IncidentStatus.Acknowledged)]
    [InlineData(IncidentStatus.Resolved)]
    public void IncidentStatus_HasExpectedValues(IncidentStatus status)
    {
        Assert.True(Enum.IsDefined(typeof(IncidentStatus), status));
    }

    [Theory]
    [InlineData(IncidentUrgency.Low)]
    [InlineData(IncidentUrgency.High)]
    public void IncidentUrgency_HasExpectedValues(IncidentUrgency urgency)
    {
        Assert.True(Enum.IsDefined(typeof(IncidentUrgency), urgency));
    }

    [Theory]
    [InlineData(IncidentSeverity.Sev1)]
    [InlineData(IncidentSeverity.Sev2)]
    [InlineData(IncidentSeverity.Sev3)]
    [InlineData(IncidentSeverity.Sev4)]
    [InlineData(IncidentSeverity.Sev5)]
    public void IncidentSeverity_HasExpectedValues(IncidentSeverity severity)
    {
        Assert.True(Enum.IsDefined(typeof(IncidentSeverity), severity));
    }

    // ========================================================================
    // Slack Provider Tests
    // ========================================================================

    [Fact]
    public void SlackProvider_ProviderName_IsCorrect()
    {
        var provider = new SlackProvider();
        Assert.Equal("slack", provider.ProviderName);
        Assert.Equal(DevOpsCategory.Communication, provider.Category);
    }

    [Fact]
    public async Task SlackProvider_ValidateCredentials_FailsWithoutBotToken()
    {
        var provider = new SlackProvider();
        var result = await provider.ValidateCredentialsAsync(new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains("Missing required bot_token", result.ErrorMessage);
    }

    // ========================================================================
    // Channel Tests
    // ========================================================================

    [Fact]
    public void Channel_HasAllProperties()
    {
        var channel = new Channel(
            "C12345",
            "general",
            "Company-wide discussions",
            "A place for general conversation",
            false,
            false,
            150,
            DateTimeOffset.UtcNow.AddYears(-2));

        Assert.Equal("C12345", channel.Id);
        Assert.Equal("general", channel.Name);
        Assert.Equal("Company-wide discussions", channel.Topic);
        Assert.False(channel.IsPrivate);
        Assert.False(channel.IsArchived);
        Assert.Equal(150, channel.MemberCount);
    }

    // ========================================================================
    // Message Tests
    // ========================================================================

    [Fact]
    public void Message_HasAllProperties()
    {
        var author = new ChatUser("U12345", "bot", "Helper Bot", null, null, true, ChatUserPresence.Active);
        var reactions = new List<MessageReaction>
        {
            new("+1", 5, ["U1", "U2", "U3", "U4", "U5"])
        };

        var message = new Message(
            "1234567890.123456",
            "C12345",
            "Hello, world!",
            author,
            DateTimeOffset.UtcNow,
            null,
            null,
            reactions,
            null,
            0);

        Assert.Equal("1234567890.123456", message.Id);
        Assert.Equal("C12345", message.ChannelId);
        Assert.Equal("Hello, world!", message.Text);
        Assert.True(message.Author?.IsBot);
        Assert.Single(message.Reactions!);
        Assert.Equal(5, message.Reactions![0].Count);
    }

    // ========================================================================
    // Chat User Tests
    // ========================================================================

    [Fact]
    public void ChatUser_HasAllProperties()
    {
        var user = new ChatUser(
            "U12345",
            "jdoe",
            "John Doe",
            "john@example.com",
            "https://avatars.com/jdoe.png",
            false,
            ChatUserPresence.Active);

        Assert.Equal("U12345", user.Id);
        Assert.Equal("jdoe", user.Name);
        Assert.Equal("John Doe", user.DisplayName);
        Assert.Equal("john@example.com", user.Email);
        Assert.False(user.IsBot);
        Assert.Equal(ChatUserPresence.Active, user.Presence);
    }

    [Theory]
    [InlineData(ChatUserPresence.Active)]
    [InlineData(ChatUserPresence.Away)]
    [InlineData(ChatUserPresence.DoNotDisturb)]
    [InlineData(ChatUserPresence.Offline)]
    public void ChatUserPresence_HasExpectedValues(ChatUserPresence presence)
    {
        Assert.True(Enum.IsDefined(typeof(ChatUserPresence), presence));
    }

    // ========================================================================
    // Request/Query Record Tests
    // ========================================================================

    [Fact]
    public void CreatePullRequestRequest_HasAllFields()
    {
        var request = new CreatePullRequestRequest(
            "Add feature X",
            "feature/x",
            "main",
            "This PR implements feature X",
            false,
            ["enhancement"],
            ["reviewer1", "reviewer2"]);

        Assert.Equal("Add feature X", request.Title);
        Assert.Equal("feature/x", request.SourceBranch);
        Assert.Equal("main", request.TargetBranch);
        Assert.False(request.IsDraft);
        Assert.Equal(2, request.Reviewers?.Count);
    }

    [Fact]
    public void CreateIssueRequest_HasAllFields()
    {
        var request = new CreateIssueRequest(
            "PROJ",
            "Fix critical bug",
            IssueType.Bug,
            "The app crashes when...",
            IssuePriority.Critical,
            "user-123",
            ["bug", "critical"],
            "PROJ-10",
            3,
            DateTimeOffset.UtcNow.AddDays(7));

        Assert.Equal("PROJ", request.ProjectKey);
        Assert.Equal("Fix critical bug", request.Title);
        Assert.Equal(IssueType.Bug, request.Type);
        Assert.Equal(IssuePriority.Critical, request.Priority);
        Assert.Equal(3, request.StoryPoints);
    }

    [Fact]
    public void CreateIncidentRequest_HasAllFields()
    {
        var request = new CreateIncidentRequest(
            "Production outage",
            "service-123",
            IncidentUrgency.High,
            "All services are down",
            "policy-456",
            ["user-1", "user-2"]);

        Assert.Equal("Production outage", request.Title);
        Assert.Equal("service-123", request.ServiceId);
        Assert.Equal(IncidentUrgency.High, request.Urgency);
        Assert.Equal(2, request.AssigneeIds?.Count);
    }

    [Fact]
    public void IssueQuery_HasAllFields()
    {
        var query = new IssueQuery(
            IssueStatusCategory.InProgress,
            IssueType.Bug,
            IssuePriority.High,
            "user-123",
            "sprint-10",
            "PROJ-5",
            ["urgent"],
            "login",
            50,
            "created DESC");

        Assert.Equal(IssueStatusCategory.InProgress, query.StatusCategory);
        Assert.Equal(IssueType.Bug, query.Type);
        Assert.Equal(50, query.MaxResults);
    }

    [Fact]
    public void IncidentQuery_HasAllFields()
    {
        var query = new IncidentQuery(
            [IncidentStatus.Triggered, IncidentStatus.Acknowledged],
            IncidentUrgency.High,
            "service-123",
            "user-456",
            DateTimeOffset.UtcNow.AddDays(-7),
            DateTimeOffset.UtcNow,
            100);

        Assert.Equal(2, query.Statuses?.Count);
        Assert.Equal(IncidentUrgency.High, query.Urgency);
        Assert.Equal(100, query.MaxResults);
    }

    // ========================================================================
    // DevOpsResourceList Tests
    // ========================================================================

    [Fact]
    public void DevOpsResourceList_HasCorrectProperties()
    {
        var items = new List<Repository>
        {
            new("1", "repo1", "org/repo1", null, "main", "", "", false, false, false, null, 0, 0, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, new("1", "org", "Organization", null))
        };

        var list = new DevOpsResourceList<Repository>(
            items,
            100,
            "next-cursor",
            DateTimeOffset.UtcNow);

        Assert.Single(list.Items);
        Assert.Equal(100, list.TotalCount);
        Assert.Equal("next-cursor", list.NextPageToken);
    }

    // ========================================================================
    // On-Call Schedule Tests
    // ========================================================================

    [Fact]
    public void OnCallSchedule_HasAllProperties()
    {
        var layers = new List<OnCallLayer>
        {
            new("layer-1", "Primary", DateTimeOffset.UtcNow, 7, ["user-1", "user-2"])
        };

        var schedule = new OnCallSchedule(
            "schedule-123",
            "Production On-Call",
            "24/7 production support",
            "America/New_York",
            layers);

        Assert.Equal("schedule-123", schedule.Id);
        Assert.Equal("Production On-Call", schedule.Name);
        Assert.Equal("America/New_York", schedule.TimeZone);
        Assert.Single(schedule.Layers);
        Assert.Equal(7, schedule.Layers[0].RotationIntervalDays);
    }

    [Fact]
    public void OnCallUser_HasAllProperties()
    {
        var user = new OnCallUser(
            "user-123",
            "Jane Doe",
            "jane@example.com",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7),
            1);

        Assert.Equal("user-123", user.Id);
        Assert.Equal("Jane Doe", user.Name);
        Assert.Equal(1, user.EscalationLevel);
    }
}
