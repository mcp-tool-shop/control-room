using System.Text.Json;
using Microsoft.Data.Sqlite;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.UseCases;

/// <summary>
/// Use case for resource sharing and permission management.
/// </summary>
public sealed class ResourceSharing
{
    private readonly Db _db;
    private readonly TeamQueries _queries;
    private readonly TeamManagement _teamManagement;

    public ResourceSharing(Db db, TeamManagement teamManagement)
    {
        _db = db;
        _queries = new TeamQueries(db);
        _teamManagement = teamManagement;
    }

    // Events
    public event EventHandler<ResourceSharedEventArgs>? ResourceShared;
    public event EventHandler<PermissionChangedEventArgs>? PermissionChanged;
    public event EventHandler<AccessRevokedEventArgs>? AccessRevoked;

    // ========================================================================
    // Resource Sharing Operations
    // ========================================================================

    public SharedResource ShareWithTeam(ResourceType resourceType, Guid resourceId, TeamId teamId, PermissionLevel permission = PermissionLevel.View)
    {
        var currentUser = _teamManagement.GetCurrentUser();

        // Verify user can share (must be member of team)
        var membership = _teamManagement.GetMembership(teamId, currentUser.Id);
        if (membership is null)
            throw new UnauthorizedAccessException("Must be a team member to share resources");

        var sharedId = SharedResourceId.New();

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Insert shared resource
        Exec(conn, tx, """
            INSERT INTO shared_resources (id, resource_type, resource_id, owner_id, shared_with_team_id, created_at)
            VALUES ($id, $resource_type, $resource_id, $owner_id, $team_id, $created_at)
            """,
            ("$id", sharedId.ToString()),
            ("$resource_type", resourceType.ToString()),
            ("$resource_id", resourceId.ToString()),
            ("$owner_id", currentUser.Id.ToString()),
            ("$team_id", teamId.ToString()),
            ("$created_at", DateTimeOffset.UtcNow.ToString("O")));

        // Add owner permission
        Exec(conn, tx, """
            INSERT INTO resource_permissions (shared_resource_id, user_id, permission_level, granted_at, granted_by)
            VALUES ($shared_id, $user_id, $permission, $granted_at, $granted_by)
            """,
            ("$shared_id", sharedId.ToString()),
            ("$user_id", currentUser.Id.ToString()),
            ("$permission", PermissionLevel.Admin.ToString()),
            ("$granted_at", DateTimeOffset.UtcNow.ToString("O")),
            ("$granted_by", currentUser.Id.ToString()));

        // Add permissions for all team members
        var members = _teamManagement.GetTeamMembers(teamId);
        foreach (var member in members.Where(m => m.UserId != currentUser.Id))
        {
            Exec(conn, tx, """
                INSERT INTO resource_permissions (shared_resource_id, user_id, permission_level, granted_at, granted_by)
                VALUES ($shared_id, $user_id, $permission, $granted_at, $granted_by)
                """,
                ("$shared_id", sharedId.ToString()),
                ("$user_id", member.UserId.ToString()),
                ("$permission", permission.ToString()),
                ("$granted_at", DateTimeOffset.UtcNow.ToString("O")),
                ("$granted_by", currentUser.Id.ToString()));
        }

        tx.Commit();

        // Log activity
        _queries.InsertActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.ResourceShared,
            $"{resourceType} shared with team",
            teamId,
            sharedId,
            null,
            null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object> { ["permission"] = permission.ToString() }
        ));

        var permissions = GetResourcePermissions(sharedId);
        var shared = new SharedResource(
            sharedId,
            resourceType,
            resourceId,
            currentUser.Id,
            teamId,
            null,
            DateTimeOffset.UtcNow,
            permissions.ToList()
        );

        ResourceShared?.Invoke(this, new ResourceSharedEventArgs { Resource = shared, TeamId = teamId, Permission = permission });

        return shared;
    }

    public SharedResource ShareWithUser(ResourceType resourceType, Guid resourceId, UserId userId, PermissionLevel permission = PermissionLevel.View)
    {
        var currentUser = _teamManagement.GetCurrentUser();
        var targetUser = _teamManagement.GetUser(userId) ?? throw new InvalidOperationException("User not found");

        var sharedId = SharedResourceId.New();

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Insert shared resource
        Exec(conn, tx, """
            INSERT INTO shared_resources (id, resource_type, resource_id, owner_id, shared_with_user_id, created_at)
            VALUES ($id, $resource_type, $resource_id, $owner_id, $user_id, $created_at)
            """,
            ("$id", sharedId.ToString()),
            ("$resource_type", resourceType.ToString()),
            ("$resource_id", resourceId.ToString()),
            ("$owner_id", currentUser.Id.ToString()),
            ("$user_id", userId.ToString()),
            ("$created_at", DateTimeOffset.UtcNow.ToString("O")));

        // Add owner permission
        Exec(conn, tx, """
            INSERT INTO resource_permissions (shared_resource_id, user_id, permission_level, granted_at, granted_by)
            VALUES ($shared_id, $user_id, $permission, $granted_at, $granted_by)
            """,
            ("$shared_id", sharedId.ToString()),
            ("$user_id", currentUser.Id.ToString()),
            ("$permission", PermissionLevel.Admin.ToString()),
            ("$granted_at", DateTimeOffset.UtcNow.ToString("O")),
            ("$granted_by", currentUser.Id.ToString()));

        // Add target user permission
        Exec(conn, tx, """
            INSERT INTO resource_permissions (shared_resource_id, user_id, permission_level, granted_at, granted_by)
            VALUES ($shared_id, $user_id, $permission, $granted_at, $granted_by)
            """,
            ("$shared_id", sharedId.ToString()),
            ("$user_id", userId.ToString()),
            ("$permission", permission.ToString()),
            ("$granted_at", DateTimeOffset.UtcNow.ToString("O")),
            ("$granted_by", currentUser.Id.ToString()));

        tx.Commit();

        // Send notification
        _queries.InsertNotification(new Notification(
            NotificationId.New(),
            userId,
            NotificationType.ResourceShared,
            $"{currentUser.DisplayName} shared a {resourceType} with you",
            false,
            null,
            sharedId,
            null,
            DateTimeOffset.UtcNow,
            null,
            new Dictionary<string, object> { ["permission"] = permission.ToString() }
        ));

        var permissions = GetResourcePermissions(sharedId);
        var shared = new SharedResource(
            sharedId,
            resourceType,
            resourceId,
            currentUser.Id,
            null,
            userId,
            DateTimeOffset.UtcNow,
            permissions.ToList()
        );

        ResourceShared?.Invoke(this, new ResourceSharedEventArgs { Resource = shared, UserId = userId, Permission = permission });

        return shared;
    }

    public SharedResource? GetSharedResource(SharedResourceId sharedResourceId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM shared_resources WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sharedResourceId.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var permissions = GetResourcePermissions(sharedResourceId);

        return new SharedResource(
            new SharedResourceId(Guid.Parse(r.GetString(r.GetOrdinal("id")))),
            Enum.Parse<ResourceType>(r.GetString(r.GetOrdinal("resource_type"))),
            Guid.Parse(r.GetString(r.GetOrdinal("resource_id"))),
            new UserId(Guid.Parse(r.GetString(r.GetOrdinal("owner_id")))),
            r.IsDBNull(r.GetOrdinal("shared_with_team_id")) ? null : new TeamId(Guid.Parse(r.GetString(r.GetOrdinal("shared_with_team_id")))),
            r.IsDBNull(r.GetOrdinal("shared_with_user_id")) ? null : new UserId(Guid.Parse(r.GetString(r.GetOrdinal("shared_with_user_id")))),
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
            permissions.ToList()
        );
    }

    public void UpdatePermission(SharedResourceId sharedResourceId, UserId userId, PermissionLevel permission)
    {
        var currentUser = _teamManagement.GetCurrentUser();

        // Check current user has admin permission
        var currentPermission = GetUserPermission(sharedResourceId, currentUser.Id);
        if (!currentPermission.CanAdmin())
            throw new UnauthorizedAccessException("Insufficient permissions");

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO resource_permissions (shared_resource_id, user_id, permission_level, granted_at, granted_by)
            VALUES ($shared_id, $user_id, $permission, $granted_at, $granted_by)
            """;
        cmd.Parameters.AddWithValue("$shared_id", sharedResourceId.ToString());
        cmd.Parameters.AddWithValue("$user_id", userId.ToString());
        cmd.Parameters.AddWithValue("$permission", permission.ToString());
        cmd.Parameters.AddWithValue("$granted_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$granted_by", currentUser.Id.ToString());
        cmd.ExecuteNonQuery();

        // Log activity
        _queries.InsertActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.PermissionChanged,
            $"Permission changed to {permission}",
            null,
            sharedResourceId,
            userId,
            null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object> { ["newPermission"] = permission.ToString() }
        ));

        // Send notification
        _queries.InsertNotification(new Notification(
            NotificationId.New(),
            userId,
            NotificationType.PermissionChanged,
            $"Your permission has been changed to {permission}",
            false,
            null,
            sharedResourceId,
            null,
            DateTimeOffset.UtcNow,
            null,
            null
        ));

        PermissionChanged?.Invoke(this, new PermissionChangedEventArgs { ResourceId = sharedResourceId, UserId = userId, NewPermission = permission });
    }

    public void RevokeAccess(SharedResourceId sharedResourceId, UserId userId)
    {
        var currentUser = _teamManagement.GetCurrentUser();

        var currentPermission = GetUserPermission(sharedResourceId, currentUser.Id);
        if (!currentPermission.CanAdmin())
            throw new UnauthorizedAccessException("Insufficient permissions");

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM resource_permissions WHERE shared_resource_id = $shared_id AND user_id = $user_id";
        cmd.Parameters.AddWithValue("$shared_id", sharedResourceId.ToString());
        cmd.Parameters.AddWithValue("$user_id", userId.ToString());
        cmd.ExecuteNonQuery();

        AccessRevoked?.Invoke(this, new AccessRevokedEventArgs { ResourceId = sharedResourceId, UserId = userId });
    }

    public void Unshare(SharedResourceId sharedResourceId)
    {
        var currentUser = _teamManagement.GetCurrentUser();
        var resource = GetSharedResource(sharedResourceId);

        if (resource is null)
            return;

        if (resource.OwnerId != currentUser.Id)
            throw new UnauthorizedAccessException("Only owner can unshare");

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Delete permissions
        Exec(conn, tx, "DELETE FROM resource_permissions WHERE shared_resource_id = $id",
            ("$id", sharedResourceId.ToString()));

        // Delete shared resource
        Exec(conn, tx, "DELETE FROM shared_resources WHERE id = $id",
            ("$id", sharedResourceId.ToString()));

        tx.Commit();

        // Log activity
        _queries.InsertActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.ResourceUnshared,
            $"{resource.ResourceType} unshared",
            resource.SharedWithTeamId,
            sharedResourceId,
            resource.SharedWithUserId,
            null,
            DateTimeOffset.UtcNow,
            null
        ));
    }

    // ========================================================================
    // Permission Query Operations
    // ========================================================================

    public PermissionLevel GetEffectivePermission(ResourceType resourceType, Guid resourceId, UserId? userId = null)
    {
        var currentUser = _teamManagement.GetCurrentUser();
        var targetUserId = userId ?? currentUser.Id;

        // Find shared resource
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM shared_resources WHERE resource_type = $type AND resource_id = $resource_id";
        cmd.Parameters.AddWithValue("$type", resourceType.ToString());
        cmd.Parameters.AddWithValue("$resource_id", resourceId.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return PermissionLevel.None;

        var sharedId = new SharedResourceId(Guid.Parse(r.GetString(r.GetOrdinal("id"))));
        var ownerId = new UserId(Guid.Parse(r.GetString(r.GetOrdinal("owner_id"))));

        // If user is owner, they have admin
        if (ownerId == targetUserId)
            return PermissionLevel.Admin;

        // Get explicit permission
        return GetUserPermission(sharedId, targetUserId);
    }

    public bool HasPermission(ResourceType resourceType, Guid resourceId, PermissionLevel requiredLevel, UserId? userId = null)
    {
        var effective = GetEffectivePermission(resourceType, resourceId, userId);
        return effective >= requiredLevel;
    }

    public IReadOnlyList<SharedResource> GetResourcesSharedWithMe(ResourceType? resourceType = null)
    {
        var currentUser = _teamManagement.GetCurrentUser();

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();

        var sql = """
            SELECT sr.* FROM shared_resources sr
            INNER JOIN resource_permissions rp ON sr.id = rp.shared_resource_id
            WHERE rp.user_id = $user_id AND sr.owner_id != $user_id
            """;

        if (resourceType.HasValue)
            sql += " AND sr.resource_type = $resource_type";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$user_id", currentUser.Id.ToString());
        if (resourceType.HasValue)
            cmd.Parameters.AddWithValue("$resource_type", resourceType.Value.ToString());

        var resources = new List<SharedResource>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var sharedId = new SharedResourceId(Guid.Parse(r.GetString(r.GetOrdinal("id"))));
            var permissions = GetResourcePermissions(sharedId);

            resources.Add(new SharedResource(
                sharedId,
                Enum.Parse<ResourceType>(r.GetString(r.GetOrdinal("resource_type"))),
                Guid.Parse(r.GetString(r.GetOrdinal("resource_id"))),
                new UserId(Guid.Parse(r.GetString(r.GetOrdinal("owner_id")))),
                r.IsDBNull(r.GetOrdinal("shared_with_team_id")) ? null : new TeamId(Guid.Parse(r.GetString(r.GetOrdinal("shared_with_team_id")))),
                r.IsDBNull(r.GetOrdinal("shared_with_user_id")) ? null : new UserId(Guid.Parse(r.GetString(r.GetOrdinal("shared_with_user_id")))),
                DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
                permissions.ToList()
            ));
        }

        return resources;
    }

    public IReadOnlyList<SharedResource> GetResourcesSharedByMe(ResourceType? resourceType = null)
    {
        var currentUser = _teamManagement.GetCurrentUser();

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();

        var sql = "SELECT * FROM shared_resources WHERE owner_id = $owner_id";

        if (resourceType.HasValue)
            sql += " AND resource_type = $resource_type";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$owner_id", currentUser.Id.ToString());
        if (resourceType.HasValue)
            cmd.Parameters.AddWithValue("$resource_type", resourceType.Value.ToString());

        var resources = new List<SharedResource>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var sharedId = new SharedResourceId(Guid.Parse(r.GetString(r.GetOrdinal("id"))));
            var permissions = GetResourcePermissions(sharedId);

            resources.Add(new SharedResource(
                sharedId,
                Enum.Parse<ResourceType>(r.GetString(r.GetOrdinal("resource_type"))),
                Guid.Parse(r.GetString(r.GetOrdinal("resource_id"))),
                new UserId(Guid.Parse(r.GetString(r.GetOrdinal("owner_id")))),
                r.IsDBNull(r.GetOrdinal("shared_with_team_id")) ? null : new TeamId(Guid.Parse(r.GetString(r.GetOrdinal("shared_with_team_id")))),
                r.IsDBNull(r.GetOrdinal("shared_with_user_id")) ? null : new UserId(Guid.Parse(r.GetString(r.GetOrdinal("shared_with_user_id")))),
                DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
                permissions.ToList()
            ));
        }

        return resources;
    }

    public IReadOnlyList<ResourcePermission> GetResourcePermissions(SharedResourceId sharedResourceId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM resource_permissions WHERE shared_resource_id = $shared_id";
        cmd.Parameters.AddWithValue("$shared_id", sharedResourceId.ToString());

        var permissions = new List<ResourcePermission>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            permissions.Add(new ResourcePermission(
                new UserId(Guid.Parse(r.GetString(r.GetOrdinal("user_id")))),
                Enum.Parse<PermissionLevel>(r.GetString(r.GetOrdinal("permission_level"))),
                DateTimeOffset.Parse(r.GetString(r.GetOrdinal("granted_at"))),
                new UserId(Guid.Parse(r.GetString(r.GetOrdinal("granted_by"))))
            ));
        }

        return permissions;
    }

    public IReadOnlyList<SharedResource> ShareMultiple(IEnumerable<(ResourceType Type, Guid Id)> resources, TeamId teamId, PermissionLevel permission)
    {
        var result = new List<SharedResource>();

        foreach (var (type, id) in resources)
        {
            var shared = ShareWithTeam(type, id, teamId, permission);
            result.Add(shared);
        }

        return result;
    }

    public void RevokeAllAccess(ResourceType resourceType, Guid resourceId)
    {
        var currentUser = _teamManagement.GetCurrentUser();

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM shared_resources
            WHERE resource_type = $type AND resource_id = $resource_id AND owner_id = $owner_id
            """;
        cmd.Parameters.AddWithValue("$type", resourceType.ToString());
        cmd.Parameters.AddWithValue("$resource_id", resourceId.ToString());
        cmd.Parameters.AddWithValue("$owner_id", currentUser.Id.ToString());

        var sharedIds = new List<SharedResourceId>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                sharedIds.Add(new SharedResourceId(Guid.Parse(r.GetString(0))));
            }
        }

        foreach (var sharedId in sharedIds)
        {
            Unshare(sharedId);
        }
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private PermissionLevel GetUserPermission(SharedResourceId sharedResourceId, UserId userId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT permission_level FROM resource_permissions WHERE shared_resource_id = $shared_id AND user_id = $user_id";
        cmd.Parameters.AddWithValue("$shared_id", sharedResourceId.ToString());
        cmd.Parameters.AddWithValue("$user_id", userId.ToString());

        var result = cmd.ExecuteScalar();
        if (result is null)
            return PermissionLevel.None;

        return Enum.Parse<PermissionLevel>(result.ToString()!);
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

// Event Args
public sealed class ResourceSharedEventArgs : EventArgs
{
    public required SharedResource Resource { get; init; }
    public TeamId? TeamId { get; init; }
    public UserId? UserId { get; init; }
    public required PermissionLevel Permission { get; init; }
}

public sealed class PermissionChangedEventArgs : EventArgs
{
    public required SharedResourceId ResourceId { get; init; }
    public required UserId UserId { get; init; }
    public required PermissionLevel NewPermission { get; init; }
}

public sealed class AccessRevokedEventArgs : EventArgs
{
    public required SharedResourceId ResourceId { get; init; }
    public required UserId UserId { get; init; }
}
