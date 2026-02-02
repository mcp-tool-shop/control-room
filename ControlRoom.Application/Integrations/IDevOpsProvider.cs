namespace ControlRoom.Application.Integrations;

// ============================================================================
// DevOps Provider Interface
// ============================================================================

/// <summary>
/// Common interface for DevOps tool integrations.
/// </summary>
public interface IDevOpsProvider
{
    /// <summary>
    /// Gets the provider name.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Gets the provider category.
    /// </summary>
    DevOpsCategory Category { get; }

    /// <summary>
    /// Validates the provided credentials/token.
    /// </summary>
    Task<DevOpsValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Test connection to the service.
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Category of DevOps tool.
/// </summary>
public enum DevOpsCategory
{
    SourceControl,
    IssueTracking,
    IncidentManagement,
    Communication,
    CiCd,
    Monitoring
}

/// <summary>
/// Result of DevOps credential validation.
/// </summary>
public sealed record DevOpsValidationResult(
    bool IsValid,
    string? ErrorMessage,
    string? UserId,
    string? UserName,
    string? OrganizationId,
    string? OrganizationName,
    IReadOnlyList<string> Scopes,
    Dictionary<string, object>? Metadata = null);

// ============================================================================
// Source Control Interface
// ============================================================================

/// <summary>
/// Interface for source control providers (GitHub, GitLab, etc.).
/// </summary>
public interface ISourceControlProvider : IDevOpsProvider
{
    /// <summary>
    /// Lists repositories.
    /// </summary>
    Task<DevOpsResourceList<Repository>> ListRepositoriesAsync(
        string? organization = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a repository by name.
    /// </summary>
    Task<Repository?> GetRepositoryAsync(
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists pull requests.
    /// </summary>
    Task<DevOpsResourceList<PullRequest>> ListPullRequestsAsync(
        string owner,
        string repo,
        PullRequestState? state = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a pull request by number.
    /// </summary>
    Task<PullRequest?> GetPullRequestAsync(
        string owner,
        string repo,
        int number,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a pull request.
    /// </summary>
    Task<PullRequest> CreatePullRequestAsync(
        string owner,
        string repo,
        CreatePullRequestRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists branches.
    /// </summary>
    Task<DevOpsResourceList<Branch>> ListBranchesAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists workflow runs (CI/CD).
    /// </summary>
    Task<DevOpsResourceList<WorkflowRun>> ListWorkflowRunsAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a workflow.
    /// </summary>
    Task<WorkflowRun> TriggerWorkflowAsync(
        string owner,
        string repo,
        string workflowId,
        string branch,
        Dictionary<string, string>? inputs = null,
        CancellationToken cancellationToken = default);
}

// ============================================================================
// Issue Tracking Interface
// ============================================================================

/// <summary>
/// Interface for issue tracking providers (Jira, Linear, etc.).
/// </summary>
public interface IIssueTrackingProvider : IDevOpsProvider
{
    /// <summary>
    /// Lists projects.
    /// </summary>
    Task<DevOpsResourceList<Project>> ListProjectsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a project by key.
    /// </summary>
    Task<Project?> GetProjectAsync(
        string projectKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists issues.
    /// </summary>
    Task<DevOpsResourceList<Issue>> ListIssuesAsync(
        string projectKey,
        IssueQuery? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an issue by key.
    /// </summary>
    Task<Issue?> GetIssueAsync(
        string issueKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an issue.
    /// </summary>
    Task<Issue> CreateIssueAsync(
        CreateIssueRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an issue.
    /// </summary>
    Task<Issue> UpdateIssueAsync(
        string issueKey,
        UpdateIssueRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a comment to an issue.
    /// </summary>
    Task<IssueComment> AddCommentAsync(
        string issueKey,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions an issue to a new status.
    /// </summary>
    Task TransitionIssueAsync(
        string issueKey,
        string transitionId,
        CancellationToken cancellationToken = default);
}

// ============================================================================
// Incident Management Interface
// ============================================================================

/// <summary>
/// Interface for incident management providers (PagerDuty, OpsGenie, etc.).
/// </summary>
public interface IIncidentManagementProvider : IDevOpsProvider
{
    /// <summary>
    /// Lists incidents.
    /// </summary>
    Task<DevOpsResourceList<Incident>> ListIncidentsAsync(
        IncidentQuery? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an incident by ID.
    /// </summary>
    Task<Incident?> GetIncidentAsync(
        string incidentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an incident.
    /// </summary>
    Task<Incident> CreateIncidentAsync(
        CreateIncidentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges an incident.
    /// </summary>
    Task<Incident> AcknowledgeIncidentAsync(
        string incidentId,
        string? message = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an incident.
    /// </summary>
    Task<Incident> ResolveIncidentAsync(
        string incidentId,
        string? resolution = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Escalates an incident.
    /// </summary>
    Task<Incident> EscalateIncidentAsync(
        string incidentId,
        string escalationPolicyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists on-call schedules.
    /// </summary>
    Task<DevOpsResourceList<OnCallSchedule>> ListOnCallSchedulesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets who is currently on call.
    /// </summary>
    Task<IReadOnlyList<OnCallUser>> GetCurrentOnCallAsync(
        string scheduleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists services.
    /// </summary>
    Task<DevOpsResourceList<IncidentService>> ListServicesAsync(
        CancellationToken cancellationToken = default);
}

// ============================================================================
// Communication Interface
// ============================================================================

/// <summary>
/// Interface for communication providers (Slack, Teams, etc.).
/// </summary>
public interface ICommunicationProvider : IDevOpsProvider
{
    /// <summary>
    /// Lists channels.
    /// </summary>
    Task<DevOpsResourceList<Channel>> ListChannelsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a channel by ID.
    /// </summary>
    Task<Channel?> GetChannelAsync(
        string channelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to a channel.
    /// </summary>
    Task<Message> SendMessageAsync(
        string channelId,
        string text,
        MessageOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a message.
    /// </summary>
    Task<Message> UpdateMessageAsync(
        string channelId,
        string messageId,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a direct message to a user.
    /// </summary>
    Task<Message> SendDirectMessageAsync(
        string userId,
        string text,
        MessageOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists users.
    /// </summary>
    Task<DevOpsResourceList<ChatUser>> ListUsersAsync(
        CancellationToken cancellationToken = default);
}

// ============================================================================
// DevOps Data Types
// ============================================================================

/// <summary>
/// Generic DevOps resource list with pagination.
/// </summary>
public sealed record DevOpsResourceList<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    string? NextPageToken,
    DateTimeOffset RetrievedAt);

// --- Source Control Types ---

/// <summary>
/// Repository information.
/// </summary>
public sealed record Repository(
    string Id,
    string Name,
    string FullName,
    string? Description,
    string DefaultBranch,
    string Url,
    string CloneUrl,
    bool IsPrivate,
    bool IsFork,
    bool IsArchived,
    string? Language,
    int StarCount,
    int ForkCount,
    int OpenIssueCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PushedAt,
    RepositoryOwner Owner,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Repository owner.
/// </summary>
public sealed record RepositoryOwner(
    string Id,
    string Login,
    string Type,
    string? AvatarUrl);

/// <summary>
/// Pull request.
/// </summary>
public sealed record PullRequest(
    int Number,
    string Id,
    string Title,
    string? Body,
    PullRequestState State,
    string SourceBranch,
    string TargetBranch,
    string Url,
    bool IsDraft,
    bool IsMergeable,
    bool IsMerged,
    DateTimeOffset? MergedAt,
    PullRequestUser Author,
    IReadOnlyList<PullRequestUser> Reviewers,
    IReadOnlyList<string> Labels,
    int CommentCount,
    int CommitCount,
    int AdditionCount,
    int DeletionCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Pull request user.
/// </summary>
public sealed record PullRequestUser(
    string Id,
    string Login,
    string? Name,
    string? AvatarUrl);

/// <summary>
/// State of a pull request.
/// </summary>
public enum PullRequestState
{
    Open,
    Closed,
    Merged,
    All
}

/// <summary>
/// Request to create a pull request.
/// </summary>
public sealed record CreatePullRequestRequest(
    string Title,
    string SourceBranch,
    string TargetBranch,
    string? Body = null,
    bool IsDraft = false,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? Reviewers = null);

/// <summary>
/// Git branch.
/// </summary>
public sealed record Branch(
    string Name,
    string CommitSha,
    bool IsProtected,
    bool IsDefault,
    DateTimeOffset? LastCommitDate);

/// <summary>
/// CI/CD workflow run.
/// </summary>
public sealed record WorkflowRun(
    string Id,
    string Name,
    string WorkflowId,
    WorkflowRunStatus Status,
    WorkflowRunConclusion? Conclusion,
    string Branch,
    string CommitSha,
    string? CommitMessage,
    string Url,
    int RunNumber,
    int? RunAttempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? Duration,
    PullRequestUser? Actor,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Workflow run status.
/// </summary>
public enum WorkflowRunStatus
{
    Queued,
    InProgress,
    Completed,
    Waiting,
    Pending
}

/// <summary>
/// Workflow run conclusion.
/// </summary>
public enum WorkflowRunConclusion
{
    Success,
    Failure,
    Cancelled,
    Skipped,
    TimedOut,
    ActionRequired
}

// --- Issue Tracking Types ---

/// <summary>
/// Project in issue tracker.
/// </summary>
public sealed record Project(
    string Id,
    string Key,
    string Name,
    string? Description,
    string? Url,
    string? IconUrl,
    ProjectOwner? Owner,
    DateTimeOffset CreatedAt,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Project owner/lead.
/// </summary>
public sealed record ProjectOwner(
    string Id,
    string Name,
    string? Email,
    string? AvatarUrl);

/// <summary>
/// Issue in tracker.
/// </summary>
public sealed record Issue(
    string Id,
    string Key,
    string Title,
    string? Description,
    IssueType Type,
    IssuePriority Priority,
    IssueStatus Status,
    string ProjectKey,
    IssueUser? Assignee,
    IssueUser Reporter,
    IReadOnlyList<string> Labels,
    int? StoryPoints,
    string? SprintId,
    string? EpicKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DueDate,
    DateTimeOffset? ResolvedAt,
    string? Url,
    Dictionary<string, object>? CustomFields = null);

/// <summary>
/// Issue user.
/// </summary>
public sealed record IssueUser(
    string Id,
    string Name,
    string? Email,
    string? AvatarUrl);

/// <summary>
/// Type of issue.
/// </summary>
public enum IssueType
{
    Bug,
    Task,
    Story,
    Epic,
    Subtask,
    Feature,
    Improvement,
    Other
}

/// <summary>
/// Issue priority.
/// </summary>
public enum IssuePriority
{
    Lowest,
    Low,
    Medium,
    High,
    Highest,
    Critical,
    Urgent,
    None
}

/// <summary>
/// Issue status.
/// </summary>
public sealed record IssueStatus(
    string Id,
    string Name,
    IssueStatusCategory Category);

/// <summary>
/// Issue status category.
/// </summary>
public enum IssueStatusCategory
{
    ToDo,
    InProgress,
    Done,
    Cancelled
}

/// <summary>
/// Query for filtering issues.
/// </summary>
public sealed record IssueQuery(
    IssueStatusCategory? StatusCategory = null,
    IssueType? Type = null,
    IssuePriority? Priority = null,
    string? AssigneeId = null,
    string? SprintId = null,
    string? EpicKey = null,
    IReadOnlyList<string>? Labels = null,
    string? SearchText = null,
    int? MaxResults = null,
    string? OrderBy = null);

/// <summary>
/// Request to create an issue.
/// </summary>
public sealed record CreateIssueRequest(
    string ProjectKey,
    string Title,
    IssueType Type,
    string? Description = null,
    IssuePriority Priority = IssuePriority.Medium,
    string? AssigneeId = null,
    IReadOnlyList<string>? Labels = null,
    string? EpicKey = null,
    int? StoryPoints = null,
    DateTimeOffset? DueDate = null,
    Dictionary<string, object>? CustomFields = null);

/// <summary>
/// Request to update an issue.
/// </summary>
public sealed record UpdateIssueRequest(
    string? Title = null,
    string? Description = null,
    IssuePriority? Priority = null,
    string? AssigneeId = null,
    IReadOnlyList<string>? Labels = null,
    int? StoryPoints = null,
    DateTimeOffset? DueDate = null,
    Dictionary<string, object>? CustomFields = null);

/// <summary>
/// Comment on an issue.
/// </summary>
public sealed record IssueComment(
    string Id,
    string Body,
    IssueUser Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

// --- Incident Management Types ---

/// <summary>
/// Incident.
/// </summary>
public sealed record Incident(
    string Id,
    string Title,
    string? Description,
    IncidentStatus Status,
    IncidentUrgency Urgency,
    IncidentSeverity Severity,
    string? ServiceId,
    string? ServiceName,
    IReadOnlyList<IncidentAssignment> Assignments,
    IncidentUser? CreatedBy,
    string? EscalationPolicyId,
    string? Url,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt,
    TimeSpan? TimeToAcknowledge,
    TimeSpan? TimeToResolve,
    int AlertCount,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Incident status.
/// </summary>
public enum IncidentStatus
{
    Triggered,
    Acknowledged,
    Resolved
}

/// <summary>
/// Incident urgency.
/// </summary>
public enum IncidentUrgency
{
    Low,
    High
}

/// <summary>
/// Incident severity.
/// </summary>
public enum IncidentSeverity
{
    Sev1,
    Sev2,
    Sev3,
    Sev4,
    Sev5,
    Unknown
}

/// <summary>
/// Incident assignment.
/// </summary>
public sealed record IncidentAssignment(
    string UserId,
    string UserName,
    string? UserEmail,
    DateTimeOffset AssignedAt);

/// <summary>
/// Incident user.
/// </summary>
public sealed record IncidentUser(
    string Id,
    string Name,
    string? Email);

/// <summary>
/// Query for filtering incidents.
/// </summary>
public sealed record IncidentQuery(
    IReadOnlyList<IncidentStatus>? Statuses = null,
    IncidentUrgency? Urgency = null,
    string? ServiceId = null,
    string? UserId = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    int? MaxResults = null);

/// <summary>
/// Request to create an incident.
/// </summary>
public sealed record CreateIncidentRequest(
    string Title,
    string ServiceId,
    IncidentUrgency Urgency = IncidentUrgency.High,
    string? Description = null,
    string? EscalationPolicyId = null,
    IReadOnlyList<string>? AssigneeIds = null);

/// <summary>
/// On-call schedule.
/// </summary>
public sealed record OnCallSchedule(
    string Id,
    string Name,
    string? Description,
    string TimeZone,
    IReadOnlyList<OnCallLayer> Layers,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// On-call schedule layer.
/// </summary>
public sealed record OnCallLayer(
    string Id,
    string Name,
    DateTimeOffset Start,
    int RotationIntervalDays,
    IReadOnlyList<string> UserIds);

/// <summary>
/// User currently on call.
/// </summary>
public sealed record OnCallUser(
    string Id,
    string Name,
    string? Email,
    DateTimeOffset Start,
    DateTimeOffset End,
    int EscalationLevel);

/// <summary>
/// Service in incident management.
/// </summary>
public sealed record IncidentService(
    string Id,
    string Name,
    string? Description,
    string Status,
    string? EscalationPolicyId,
    DateTimeOffset CreatedAt,
    Dictionary<string, object>? Metadata = null);

// --- Communication Types ---

/// <summary>
/// Chat channel.
/// </summary>
public sealed record Channel(
    string Id,
    string Name,
    string? Topic,
    string? Purpose,
    bool IsPrivate,
    bool IsArchived,
    int MemberCount,
    DateTimeOffset CreatedAt,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Chat message.
/// </summary>
public sealed record Message(
    string Id,
    string ChannelId,
    string Text,
    ChatUser? Author,
    DateTimeOffset Timestamp,
    DateTimeOffset? EditedAt,
    IReadOnlyList<MessageAttachment>? Attachments,
    IReadOnlyList<MessageReaction>? Reactions,
    string? ThreadId,
    int ReplyCount,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Chat user.
/// </summary>
public sealed record ChatUser(
    string Id,
    string Name,
    string? DisplayName,
    string? Email,
    string? AvatarUrl,
    bool IsBot,
    ChatUserPresence Presence);

/// <summary>
/// User presence status.
/// </summary>
public enum ChatUserPresence
{
    Active,
    Away,
    DoNotDisturb,
    Offline
}

/// <summary>
/// Message attachment.
/// </summary>
public sealed record MessageAttachment(
    string? Title,
    string? Text,
    string? Color,
    string? ImageUrl,
    IReadOnlyList<AttachmentField>? Fields);

/// <summary>
/// Attachment field.
/// </summary>
public sealed record AttachmentField(
    string Title,
    string Value,
    bool IsShort);

/// <summary>
/// Message reaction.
/// </summary>
public sealed record MessageReaction(
    string Name,
    int Count,
    IReadOnlyList<string> UserIds);

/// <summary>
/// Options for sending a message.
/// </summary>
public sealed record MessageOptions(
    IReadOnlyList<MessageAttachment>? Attachments = null,
    string? ThreadId = null,
    bool? UnfurlLinks = null,
    bool? UnfurlMedia = null);
