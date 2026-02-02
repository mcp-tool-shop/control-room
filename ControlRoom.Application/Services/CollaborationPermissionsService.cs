using System.Collections.Concurrent;
using System.Text.Json;

namespace ControlRoom.Application.Services;

/// <summary>
/// Collaboration & Permissions: Ensures clear, understandable permission model
/// with attribution and audit trails.
///
/// Checklist items addressed:
/// - Permissions understandable
/// - Least privilege by default
/// - Who can see/edit/execute is clear
/// - Changes are auditable
/// - Actions attributed to users
/// - History preserved
/// </summary>
public sealed class CollaborationPermissionsService
{
    private readonly IPermissionRepository _permissionRepository;
    private readonly IAuditLogRepository _auditRepository;
    private readonly ConcurrentDictionary<string, PermissionCache> _permissionCache = new();

    public event EventHandler<PermissionChangedEventArgs>? PermissionChanged;
    public event EventHandler<AuditEventArgs>? AuditEvent;

    public CollaborationPermissionsService(
        IPermissionRepository permissionRepository,
        IAuditLogRepository auditRepository)
    {
        _permissionRepository = permissionRepository;
        _auditRepository = auditRepository;
    }

    // ========================================================================
    // ROLES: Understandable Permissions with Least Privilege
    // ========================================================================

    /// <summary>
    /// Gets all available roles with human-readable descriptions.
    /// </summary>
    public IReadOnlyList<RoleDefinition> GetRoles()
    {
        return
        [
            new RoleDefinition(
                Id: "viewer",
                Name: "Viewer",
                Description: "Can view dashboards and read-only access to resources",
                Level: PermissionLevel.Read,
                Capabilities: [
                    new Capability("view_dashboards", "View dashboards and metrics"),
                    new Capability("view_runbooks", "View runbook definitions"),
                    new Capability("view_history", "View execution history"),
                    new Capability("export_data", "Export reports and data")
                ],
                Restrictions: [
                    "Cannot modify any resources",
                    "Cannot execute runbooks",
                    "Cannot access sensitive credentials"
                ],
                IsDefault: true),

            new RoleDefinition(
                Id: "operator",
                Name: "Operator",
                Description: "Can execute runbooks and manage day-to-day operations",
                Level: PermissionLevel.Execute,
                Capabilities: [
                    new Capability("view_dashboards", "View dashboards and metrics"),
                    new Capability("view_runbooks", "View runbook definitions"),
                    new Capability("execute_runbooks", "Execute approved runbooks"),
                    new Capability("pause_executions", "Pause and resume executions"),
                    new Capability("view_logs", "View detailed execution logs"),
                    new Capability("manage_alerts", "Acknowledge and manage alerts")
                ],
                Restrictions: [
                    "Cannot create or modify runbooks",
                    "Cannot modify integrations",
                    "Cannot manage team members"
                ],
                IsDefault: false),

            new RoleDefinition(
                Id: "developer",
                Name: "Developer",
                Description: "Can create and modify runbooks and automation",
                Level: PermissionLevel.Write,
                Capabilities: [
                    new Capability("view_dashboards", "View dashboards and metrics"),
                    new Capability("create_runbooks", "Create new runbooks"),
                    new Capability("edit_runbooks", "Modify existing runbooks"),
                    new Capability("execute_runbooks", "Execute runbooks"),
                    new Capability("manage_schedules", "Create and modify schedules"),
                    new Capability("view_audit_logs", "View audit history")
                ],
                Restrictions: [
                    "Cannot modify production environments without approval",
                    "Cannot manage integrations",
                    "Cannot change permissions"
                ],
                IsDefault: false),

            new RoleDefinition(
                Id: "admin",
                Name: "Administrator",
                Description: "Full access to manage team, integrations, and settings",
                Level: PermissionLevel.Admin,
                Capabilities: [
                    new Capability("all_operations", "All viewer, operator, and developer capabilities"),
                    new Capability("manage_team", "Add, remove, and modify team members"),
                    new Capability("manage_integrations", "Configure external integrations"),
                    new Capability("manage_permissions", "Assign roles and permissions"),
                    new Capability("view_audit_logs", "Full audit log access"),
                    new Capability("manage_settings", "Configure application settings")
                ],
                Restrictions: [
                    "Cannot delete organization",
                    "Cannot remove last admin"
                ],
                IsDefault: false),

            new RoleDefinition(
                Id: "owner",
                Name: "Owner",
                Description: "Organization owner with full control",
                Level: PermissionLevel.Owner,
                Capabilities: [
                    new Capability("full_access", "Complete access to all features"),
                    new Capability("manage_billing", "Manage subscription and billing"),
                    new Capability("delete_organization", "Delete organization"),
                    new Capability("transfer_ownership", "Transfer ownership to another user")
                ],
                Restrictions: [],
                IsDefault: false)
        ];
    }

    /// <summary>
    /// Checks if a user has a specific permission.
    /// Returns a clear explanation of why or why not.
    /// </summary>
    public async Task<PermissionCheckResult> CheckPermissionAsync(
        string userId,
        string permission,
        string? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        // Get user's effective permissions
        var effectivePermissions = await GetEffectivePermissionsAsync(userId, cancellationToken);

        // Check direct permission
        if (effectivePermissions.HasPermission(permission, resourceId))
        {
            return new PermissionCheckResult(
                Allowed: true,
                Reason: $"Granted via role: {effectivePermissions.PrimaryRole}",
                GrantedBy: effectivePermissions.PrimaryRole,
                GrantedAt: effectivePermissions.RoleAssignedAt);
        }

        // Check for inherited permission
        var inheritedFrom = effectivePermissions.GetInheritedPermissionSource(permission);
        if (inheritedFrom != null)
        {
            return new PermissionCheckResult(
                Allowed: true,
                Reason: $"Inherited from: {inheritedFrom}",
                GrantedBy: inheritedFrom,
                GrantedAt: effectivePermissions.RoleAssignedAt);
        }

        // Not allowed - explain what role would be needed
        var requiredRole = GetRequiredRoleForPermission(permission);
        return new PermissionCheckResult(
            Allowed: false,
            Reason: $"Requires '{requiredRole}' role or higher",
            RequiredRole: requiredRole,
            CurrentRole: effectivePermissions.PrimaryRole);
    }

    /// <summary>
    /// Assigns a role to a user with least-privilege defaults.
    /// </summary>
    public async Task<RoleAssignmentResult> AssignRoleAsync(
        string userId,
        string roleId,
        string assignedBy,
        RoleAssignmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RoleAssignmentOptions();
        var roles = GetRoles();
        var role = roles.FirstOrDefault(r => r.Id == roleId);

        if (role == null)
        {
            return new RoleAssignmentResult(
                Success: false,
                Message: $"Unknown role: {roleId}",
                PreviousRole: null,
                NewRole: null);
        }

        // Check if assigner has permission to assign this role
        var assignerCheck = await CheckPermissionAsync(
            assignedBy, "manage_permissions", cancellationToken: cancellationToken);

        if (!assignerCheck.Allowed)
        {
            return new RoleAssignmentResult(
                Success: false,
                Message: "You don't have permission to assign roles",
                PreviousRole: null,
                NewRole: null);
        }

        // Get previous role for audit
        var previousPermissions = await GetEffectivePermissionsAsync(userId, cancellationToken);
        var previousRole = previousPermissions.PrimaryRole;

        // Assign the role
        var assignment = new RoleAssignment
        {
            UserId = userId,
            RoleId = roleId,
            AssignedBy = assignedBy,
            AssignedAt = DateTimeOffset.UtcNow,
            ExpiresAt = opts.ExpiresAt,
            Scope = opts.Scope ?? PermissionScope.Organization,
            ResourceIds = opts.ResourceIds
        };

        await _permissionRepository.AssignRoleAsync(assignment, cancellationToken);

        // Audit the change
        await AuditAsync(new AuditEntry
        {
            Action = AuditAction.RoleAssigned,
            ActorId = assignedBy,
            TargetUserId = userId,
            Details = new Dictionary<string, object>
            {
                ["previousRole"] = previousRole,
                ["newRole"] = roleId,
                ["scope"] = opts.Scope?.ToString() ?? "Organization"
            }
        }, cancellationToken);

        // Invalidate cache
        _permissionCache.TryRemove(userId, out _);

        OnPermissionChanged(userId, previousRole, roleId);

        return new RoleAssignmentResult(
            Success: true,
            Message: $"Role '{role.Name}' assigned successfully",
            PreviousRole: previousRole,
            NewRole: roleId);
    }

    // ========================================================================
    // VISIBILITY: Clear Permission Display
    // ========================================================================

    /// <summary>
    /// Gets a clear view of who can access a resource.
    /// </summary>
    public async Task<ResourceAccessView> GetResourceAccessAsync(
        string resourceId,
        string resourceType,
        CancellationToken cancellationToken = default)
    {
        var access = await _permissionRepository.GetResourceAccessAsync(resourceId, cancellationToken);

        var viewers = new List<UserAccessInfo>();
        var editors = new List<UserAccessInfo>();
        var executors = new List<UserAccessInfo>();

        foreach (var user in access)
        {
            var info = new UserAccessInfo(
                UserId: user.UserId,
                UserName: user.UserName,
                Role: user.Role,
                GrantedAt: user.GrantedAt,
                GrantedBy: user.GrantedBy);

            switch (GetEffectiveAccessLevel(user.Role))
            {
                case PermissionLevel.Read:
                    viewers.Add(info);
                    break;
                case PermissionLevel.Execute:
                    executors.Add(info);
                    viewers.Add(info);
                    break;
                case PermissionLevel.Write:
                case PermissionLevel.Admin:
                case PermissionLevel.Owner:
                    editors.Add(info);
                    executors.Add(info);
                    viewers.Add(info);
                    break;
            }
        }

        return new ResourceAccessView(
            ResourceId: resourceId,
            ResourceType: resourceType,
            CanView: viewers.DistinctBy(u => u.UserId).ToList(),
            CanEdit: editors.DistinctBy(u => u.UserId).ToList(),
            CanExecute: executors.DistinctBy(u => u.UserId).ToList(),
            IsPublic: access.Any(a => a.Role == "public"),
            AccessSummary: GenerateAccessSummary(viewers.Count, editors.Count, executors.Count));
    }

    /// <summary>
    /// Gets a user's complete permission summary.
    /// </summary>
    public async Task<UserPermissionSummary> GetUserPermissionSummaryAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var permissions = await GetEffectivePermissionsAsync(userId, cancellationToken);
        var roles = GetRoles();
        var currentRole = roles.FirstOrDefault(r => r.Id == permissions.PrimaryRole);

        return new UserPermissionSummary(
            UserId: userId,
            PrimaryRole: permissions.PrimaryRole,
            RoleDescription: currentRole?.Description ?? "Unknown role",
            Level: currentRole?.Level ?? PermissionLevel.Read,
            Capabilities: currentRole?.Capabilities ?? [],
            Restrictions: currentRole?.Restrictions ?? [],
            AdditionalPermissions: permissions.AdditionalPermissions,
            EffectiveScopes: permissions.Scopes,
            CanDo: GenerateCanDoList(permissions),
            CannotDo: GenerateCannotDoList(permissions, roles));
    }

    // ========================================================================
    // ACTIVITY: Attribution & Audit Trail
    // ========================================================================

    /// <summary>
    /// Records an auditable action.
    /// </summary>
    public async Task<string> AuditAsync(
        AuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        entry.Id = Guid.NewGuid().ToString("N");
        entry.Timestamp = DateTimeOffset.UtcNow;

        await _auditRepository.RecordAsync(entry, cancellationToken);
        OnAuditEvent(entry);

        return entry.Id;
    }

    /// <summary>
    /// Gets audit history for a resource.
    /// </summary>
    public async Task<AuditHistory> GetAuditHistoryAsync(
        AuditHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var entries = await _auditRepository.QueryAsync(query, cancellationToken);
        var totalCount = await _auditRepository.CountAsync(query, cancellationToken);

        return new AuditHistory(
            Entries: entries,
            TotalCount: totalCount,
            HasMore: entries.Count < totalCount,
            Query: query);
    }

    /// <summary>
    /// Gets recent activity by a user.
    /// </summary>
    public async Task<UserActivity> GetUserActivityAsync(
        string userId,
        TimeSpan? window = null,
        int maxEntries = 50,
        CancellationToken cancellationToken = default)
    {
        var query = new AuditHistoryQuery
        {
            ActorId = userId,
            Since = DateTimeOffset.UtcNow.Subtract(window ?? TimeSpan.FromDays(7)),
            MaxResults = maxEntries
        };

        var entries = await _auditRepository.QueryAsync(query, cancellationToken);

        // Group by action type
        var byAction = entries.GroupBy(e => e.Action)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        // Recent summary
        var recentActions = entries.Take(10).Select(e => new ActivitySummary(
            Action: e.Action.ToString(),
            Description: FormatAuditEntry(e),
            Timestamp: e.Timestamp,
            ResourceId: e.ResourceId)).ToList();

        return new UserActivity(
            UserId: userId,
            ActionCounts: byAction,
            RecentActions: recentActions,
            TotalActions: entries.Count,
            FirstAction: entries.LastOrDefault()?.Timestamp,
            LastAction: entries.FirstOrDefault()?.Timestamp);
    }

    /// <summary>
    /// Gets who made changes to a resource.
    /// </summary>
    public async Task<ChangeHistory> GetChangeHistoryAsync(
        string resourceId,
        int maxEntries = 100,
        CancellationToken cancellationToken = default)
    {
        var query = new AuditHistoryQuery
        {
            ResourceId = resourceId,
            Actions = [AuditAction.Created, AuditAction.Modified, AuditAction.Deleted],
            MaxResults = maxEntries
        };

        var entries = await _auditRepository.QueryAsync(query, cancellationToken);

        var changes = entries.Select(e => new ChangeRecord(
            ChangeId: e.Id,
            ActorId: e.ActorId,
            ActorName: e.ActorName,
            Action: e.Action,
            Timestamp: e.Timestamp,
            Summary: FormatChangeDescription(e),
            Details: e.Details)).ToList();

        return new ChangeHistory(
            ResourceId: resourceId,
            Changes: changes,
            CreatedBy: changes.LastOrDefault(c => c.Action == AuditAction.Created)?.ActorName,
            CreatedAt: changes.LastOrDefault(c => c.Action == AuditAction.Created)?.Timestamp,
            LastModifiedBy: changes.FirstOrDefault(c => c.Action == AuditAction.Modified)?.ActorName,
            LastModifiedAt: changes.FirstOrDefault(c => c.Action == AuditAction.Modified)?.Timestamp);
    }

    // ========================================================================
    // HELPERS: Permission Explanations
    // ========================================================================

    /// <summary>
    /// Explains why a user has or doesn't have a permission.
    /// </summary>
    public async Task<PermissionExplanation> ExplainPermissionAsync(
        string userId,
        string permission,
        string? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        var check = await CheckPermissionAsync(userId, permission, resourceId, cancellationToken);
        var permissions = await GetEffectivePermissionsAsync(userId, cancellationToken);
        var roles = GetRoles();
        var currentRole = roles.FirstOrDefault(r => r.Id == permissions.PrimaryRole);

        if (check.Allowed)
        {
            return new PermissionExplanation(
                Permission: permission,
                IsAllowed: true,
                Summary: $"You have '{permission}' permission",
                Details: new List<string?>
                {
                    $"Your role: {currentRole?.Name ?? permissions.PrimaryRole}",
                    $"Granted by: {check.GrantedBy}",
                    check.GrantedAt.HasValue ? $"Since: {check.GrantedAt.Value:g}" : null
                }.Where(d => d != null).Cast<string>().ToList(),
                Suggestions: new List<string>());
        }

        var requiredRole = roles.FirstOrDefault(r => r.Id == check.RequiredRole);
        return new PermissionExplanation(
            Permission: permission,
            IsAllowed: false,
            Summary: $"You don't have '{permission}' permission",
            Details: [
                $"Your current role: {currentRole?.Name ?? permissions.PrimaryRole}",
                $"Required role: {requiredRole?.Name ?? check.RequiredRole}",
                $"Your role level: {currentRole?.Level}",
                $"Required level: {requiredRole?.Level}"
            ],
            Suggestions: [
                $"Request '{requiredRole?.Name}' role from an administrator",
                "Contact your team admin for elevated access"
            ]);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private async Task<EffectivePermissions> GetEffectivePermissionsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        // Check cache
        if (_permissionCache.TryGetValue(userId, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Permissions;
        }

        // Load from repository
        var permissions = await _permissionRepository.GetEffectivePermissionsAsync(userId, cancellationToken);

        // Cache for 5 minutes
        _permissionCache[userId] = new PermissionCache(
            permissions,
            DateTimeOffset.UtcNow.AddMinutes(5));

        return permissions;
    }

    private string GetRequiredRoleForPermission(string permission)
    {
        return permission switch
        {
            "view_dashboards" or "view_runbooks" or "view_history" => "viewer",
            "execute_runbooks" or "pause_executions" or "manage_alerts" => "operator",
            "create_runbooks" or "edit_runbooks" or "manage_schedules" => "developer",
            "manage_team" or "manage_integrations" or "manage_permissions" => "admin",
            "manage_billing" or "delete_organization" => "owner",
            _ => "admin"
        };
    }

    private static PermissionLevel GetEffectiveAccessLevel(string role)
    {
        return role switch
        {
            "viewer" => PermissionLevel.Read,
            "operator" => PermissionLevel.Execute,
            "developer" => PermissionLevel.Write,
            "admin" => PermissionLevel.Admin,
            "owner" => PermissionLevel.Owner,
            _ => PermissionLevel.Read
        };
    }

    private static string GenerateAccessSummary(int viewers, int editors, int executors)
    {
        var parts = new List<string>();
        if (editors > 0) parts.Add($"{editors} can edit");
        if (executors > editors) parts.Add($"{executors - editors} can execute");
        if (viewers > executors) parts.Add($"{viewers - executors} can view");
        return string.Join(", ", parts);
    }

    private static IReadOnlyList<string> GenerateCanDoList(EffectivePermissions permissions)
    {
        return permissions.AllPermissions
            .Select(p => FormatPermissionAsAction(p))
            .ToList();
    }

    private static IReadOnlyList<string> GenerateCannotDoList(
        EffectivePermissions permissions,
        IReadOnlyList<RoleDefinition> roles)
    {
        var allCapabilities = roles
            .SelectMany(r => r.Capabilities)
            .Select(c => c.Id)
            .Distinct();

        return allCapabilities
            .Where(c => !permissions.AllPermissions.Contains(c))
            .Select(p => FormatPermissionAsAction(p))
            .ToList();
    }

    private static string FormatPermissionAsAction(string permission)
    {
        return permission.Replace("_", " ").ToLowerInvariant() switch
        {
            var s when s.StartsWith("view") => $"View {s[5..]}",
            var s when s.StartsWith("create") => $"Create {s[7..]}",
            var s when s.StartsWith("edit") => $"Edit {s[5..]}",
            var s when s.StartsWith("execute") => $"Execute {s[8..]}",
            var s when s.StartsWith("manage") => $"Manage {s[7..]}",
            var s => char.ToUpper(s[0]) + s[1..]
        };
    }

    private static string FormatAuditEntry(AuditEntry entry)
    {
        return entry.Action switch
        {
            AuditAction.Created => $"Created {entry.ResourceType}",
            AuditAction.Modified => $"Modified {entry.ResourceType}",
            AuditAction.Deleted => $"Deleted {entry.ResourceType}",
            AuditAction.Executed => $"Executed {entry.ResourceType}",
            AuditAction.RoleAssigned => "Assigned role",
            AuditAction.PermissionGranted => "Granted permission",
            AuditAction.PermissionRevoked => "Revoked permission",
            AuditAction.Login => "Logged in",
            AuditAction.Logout => "Logged out",
            _ => entry.Action.ToString()
        };
    }

    private static string FormatChangeDescription(AuditEntry entry)
    {
        var actionDesc = entry.Action switch
        {
            AuditAction.Created => "created",
            AuditAction.Modified => "modified",
            AuditAction.Deleted => "deleted",
            _ => entry.Action.ToString().ToLowerInvariant()
        };

        return $"{entry.ActorName ?? "Someone"} {actionDesc} this {entry.ResourceType?.ToLowerInvariant() ?? "resource"}";
    }

    private void OnPermissionChanged(string userId, string previousRole, string newRole)
    {
        PermissionChanged?.Invoke(this, new PermissionChangedEventArgs(
            userId, previousRole, newRole, DateTimeOffset.UtcNow));
    }

    private void OnAuditEvent(AuditEntry entry)
    {
        AuditEvent?.Invoke(this, new AuditEventArgs(entry));
    }
}

// ============================================================================
// Collaboration & Permissions Types
// ============================================================================

/// <summary>
/// Role definition with clear descriptions.
/// </summary>
public sealed record RoleDefinition(
    string Id,
    string Name,
    string Description,
    PermissionLevel Level,
    IReadOnlyList<Capability> Capabilities,
    IReadOnlyList<string> Restrictions,
    bool IsDefault);

/// <summary>
/// Capability (permission) definition.
/// </summary>
public sealed record Capability(
    string Id,
    string Description);

/// <summary>
/// Permission level hierarchy.
/// </summary>
public enum PermissionLevel
{
    Read = 1,
    Execute = 2,
    Write = 3,
    Admin = 4,
    Owner = 5
}

/// <summary>
/// Permission scope.
/// </summary>
public enum PermissionScope
{
    Resource,
    Project,
    Team,
    Organization
}

/// <summary>
/// Permission check result.
/// </summary>
public sealed record PermissionCheckResult(
    bool Allowed,
    string Reason,
    string? GrantedBy = null,
    DateTimeOffset? GrantedAt = null,
    string? RequiredRole = null,
    string? CurrentRole = null);

/// <summary>
/// Role assignment.
/// </summary>
public sealed class RoleAssignment
{
    public required string UserId { get; set; }
    public required string RoleId { get; set; }
    public required string AssignedBy { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public PermissionScope Scope { get; set; }
    public IReadOnlyList<string>? ResourceIds { get; set; }
}

/// <summary>
/// Role assignment options.
/// </summary>
public sealed record RoleAssignmentOptions(
    DateTimeOffset? ExpiresAt = null,
    PermissionScope? Scope = null,
    IReadOnlyList<string>? ResourceIds = null);

/// <summary>
/// Role assignment result.
/// </summary>
public sealed record RoleAssignmentResult(
    bool Success,
    string Message,
    string? PreviousRole,
    string? NewRole);

/// <summary>
/// Resource access view.
/// </summary>
public sealed record ResourceAccessView(
    string ResourceId,
    string ResourceType,
    IReadOnlyList<UserAccessInfo> CanView,
    IReadOnlyList<UserAccessInfo> CanEdit,
    IReadOnlyList<UserAccessInfo> CanExecute,
    bool IsPublic,
    string AccessSummary);

/// <summary>
/// User access info.
/// </summary>
public sealed record UserAccessInfo(
    string UserId,
    string UserName,
    string Role,
    DateTimeOffset GrantedAt,
    string GrantedBy);

/// <summary>
/// User permission summary.
/// </summary>
public sealed record UserPermissionSummary(
    string UserId,
    string PrimaryRole,
    string RoleDescription,
    PermissionLevel Level,
    IReadOnlyList<Capability> Capabilities,
    IReadOnlyList<string> Restrictions,
    IReadOnlyList<string> AdditionalPermissions,
    IReadOnlyList<PermissionScope> EffectiveScopes,
    IReadOnlyList<string> CanDo,
    IReadOnlyList<string> CannotDo);

/// <summary>
/// Permission explanation.
/// </summary>
public sealed record PermissionExplanation(
    string Permission,
    bool IsAllowed,
    string Summary,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Suggestions);

/// <summary>
/// Effective permissions for a user.
/// </summary>
public sealed class EffectivePermissions
{
    public required string UserId { get; set; }
    public required string PrimaryRole { get; set; }
    public DateTimeOffset RoleAssignedAt { get; set; }
    public required IReadOnlyList<string> AllPermissions { get; set; }
    public required IReadOnlyList<string> AdditionalPermissions { get; set; }
    public required IReadOnlyList<PermissionScope> Scopes { get; set; }
    public Dictionary<string, string>? InheritedPermissions { get; set; }

    public bool HasPermission(string permission, string? resourceId = null)
    {
        return AllPermissions.Contains(permission) ||
               AllPermissions.Contains("all_operations") ||
               AllPermissions.Contains("full_access");
    }

    public string? GetInheritedPermissionSource(string permission)
    {
        return InheritedPermissions?.GetValueOrDefault(permission);
    }
}

/// <summary>
/// Permission cache entry.
/// </summary>
internal sealed record PermissionCache(
    EffectivePermissions Permissions,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Audit entry.
/// </summary>
public sealed class AuditEntry
{
    public string Id { get; set; } = "";
    public AuditAction Action { get; set; }
    public required string ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? TargetUserId { get; set; }
    public string? ResourceId { get; set; }
    public string? ResourceType { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Dictionary<string, object>? Details { get; set; }
}

/// <summary>
/// Audit action types.
/// </summary>
public enum AuditAction
{
    Created,
    Modified,
    Deleted,
    Executed,
    Viewed,
    RoleAssigned,
    RoleRevoked,
    PermissionGranted,
    PermissionRevoked,
    Login,
    Logout,
    SettingsChanged,
    IntegrationConnected,
    IntegrationDisconnected,
    SecretCreated,
    SecretAccessed,
    SecretRotated,
    AccessDenied,
    TenantBoundaryViolation,
    WebhookVerified,
    WebhookRejected
}

/// <summary>
/// Audit history query.
/// </summary>
public sealed class AuditHistoryQuery
{
    public string? ActorId { get; set; }
    public string? ResourceId { get; set; }
    public IReadOnlyList<AuditAction>? Actions { get; set; }
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset? Until { get; set; }
    public int MaxResults { get; set; } = 100;
    public int Offset { get; set; }
}

/// <summary>
/// Audit history result.
/// </summary>
public sealed record AuditHistory(
    IReadOnlyList<AuditEntry> Entries,
    int TotalCount,
    bool HasMore,
    AuditHistoryQuery Query);

/// <summary>
/// User activity summary.
/// </summary>
public sealed record UserActivity(
    string UserId,
    IReadOnlyDictionary<string, int> ActionCounts,
    IReadOnlyList<ActivitySummary> RecentActions,
    int TotalActions,
    DateTimeOffset? FirstAction,
    DateTimeOffset? LastAction);

/// <summary>
/// Activity summary.
/// </summary>
public sealed record ActivitySummary(
    string Action,
    string Description,
    DateTimeOffset Timestamp,
    string? ResourceId);

/// <summary>
/// Change history for a resource.
/// </summary>
public sealed record ChangeHistory(
    string ResourceId,
    IReadOnlyList<ChangeRecord> Changes,
    string? CreatedBy,
    DateTimeOffset? CreatedAt,
    string? LastModifiedBy,
    DateTimeOffset? LastModifiedAt);

/// <summary>
/// Change record.
/// </summary>
public sealed record ChangeRecord(
    string ChangeId,
    string ActorId,
    string? ActorName,
    AuditAction Action,
    DateTimeOffset Timestamp,
    string Summary,
    Dictionary<string, object>? Details);

/// <summary>
/// User resource access.
/// </summary>
public sealed class UserResourceAccess
{
    public required string UserId { get; set; }
    public required string UserName { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public required string GrantedBy { get; set; }
}

// ============================================================================
// Events
// ============================================================================

public sealed class PermissionChangedEventArgs : EventArgs
{
    public string UserId { get; }
    public string PreviousRole { get; }
    public string NewRole { get; }
    public DateTimeOffset Timestamp { get; }

    public PermissionChangedEventArgs(string userId, string previousRole, string newRole, DateTimeOffset timestamp)
    {
        UserId = userId;
        PreviousRole = previousRole;
        NewRole = newRole;
        Timestamp = timestamp;
    }
}

public sealed class AuditEventArgs : EventArgs
{
    public AuditEntry Entry { get; }
    public AuditEventArgs(AuditEntry entry) => Entry = entry;
}

// ============================================================================
// Interfaces
// ============================================================================

public interface IPermissionRepository
{
    Task AssignRoleAsync(RoleAssignment assignment, CancellationToken cancellationToken);
    Task<EffectivePermissions> GetEffectivePermissionsAsync(string userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserResourceAccess>> GetResourceAccessAsync(string resourceId, CancellationToken cancellationToken);
}

public interface IAuditLogRepository
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditHistoryQuery query, CancellationToken cancellationToken);
    Task<int> CountAsync(AuditHistoryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntry>> GetEntriesAsync(DateTimeOffset from, DateTimeOffset to, AuditAction[] actions, CancellationToken cancellationToken);
}
