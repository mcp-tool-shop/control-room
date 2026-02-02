using System.Text.Json;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.UseCases;

/// <summary>
/// Use case for team management operations.
/// </summary>
public sealed class TeamManagement
{
    private readonly Db _db;
    private readonly TeamQueries _queries;
    private User? _currentUser;

    public TeamManagement(Db db)
    {
        _db = db;
        _queries = new TeamQueries(db);
    }

    // Events
    public event EventHandler<TeamCreatedEventArgs>? TeamCreated;
    public event EventHandler<MemberAddedEventArgs>? MemberAdded;
    public event EventHandler<MemberRemovedEventArgs>? MemberRemoved;
    public event EventHandler<InvitationSentEventArgs>? InvitationSent;

    // ========================================================================
    // User Operations
    // ========================================================================

    public User GetCurrentUser()
    {
        if (_currentUser is not null)
            return _currentUser;

        // Try to get user from storage or create default
        var existingUser = _queries.GetUserByUsername(Environment.UserName);
        if (existingUser is not null)
        {
            _currentUser = existingUser;
            return _currentUser;
        }

        // Create default user
        _currentUser = CreateUser(
            Environment.UserName,
            Environment.UserName,
            $"{Environment.UserName}@local"
        );

        return _currentUser;
    }

    public User? GetUser(UserId userId) => _queries.GetUser(userId);

    public User? GetUserByUsername(string username) => _queries.GetUserByUsername(username);

    public IReadOnlyList<User> SearchUsers(string query, int limit = 20) => _queries.SearchUsers(query, limit);

    public User CreateUser(string username, string displayName, string email)
    {
        var user = new User(
            UserId.New(),
            username,
            displayName,
            email,
            UserRole.User,
            DateTimeOffset.UtcNow,
            null,
            new Dictionary<string, string>()
        );

        _queries.InsertUser(user);

        LogActivity(new ActivityEntry(
            ActivityId.New(),
            user.Id,
            ActivityType.UserCreated,
            $"User {username} created",
            null, null, null, null,
            DateTimeOffset.UtcNow,
            null
        ));

        return user;
    }

    public User UpdateUser(UserId userId, string? displayName = null, string? email = null, Dictionary<string, string>? preferences = null)
    {
        var user = _queries.GetUser(userId) ?? throw new InvalidOperationException("User not found");

        var updated = user with
        {
            DisplayName = displayName ?? user.DisplayName,
            Email = email ?? user.Email,
            Preferences = preferences ?? user.Preferences
        };

        _queries.UpdateUser(updated);

        return updated;
    }

    public void UpdateLastLogin(UserId userId)
    {
        var user = _queries.GetUser(userId);
        if (user is null) return;

        var updated = user with { LastLoginAt = DateTimeOffset.UtcNow };
        _queries.UpdateUser(updated);
    }

    // ========================================================================
    // Team Operations
    // ========================================================================

    public Team CreateTeam(string name, string? description = null)
    {
        var currentUser = GetCurrentUser();

        var team = new Team(
            TeamId.New(),
            name,
            description ?? "",
            currentUser.Id,
            DateTimeOffset.UtcNow,
            null,
            new Dictionary<string, string>(),
            new List<TeamMembership>()
        );

        _queries.InsertTeam(team);

        // Add owner as admin member
        var membership = AddMember(team.Id, currentUser.Id, TeamRole.Admin);

        LogActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.TeamCreated,
            $"Team '{name}' created",
            team.Id, null, null, null,
            DateTimeOffset.UtcNow,
            null
        ));

        TeamCreated?.Invoke(this, new TeamCreatedEventArgs { Team = team, Creator = currentUser });

        return team with { Members = new List<TeamMembership> { membership } };
    }

    public Team? GetTeam(TeamId teamId) => _queries.GetTeam(teamId);

    public IReadOnlyList<Team> GetUserTeams(UserId? userId = null)
    {
        var currentUser = GetCurrentUser();
        return _queries.GetUserTeams(userId ?? currentUser.Id);
    }

    public Team UpdateTeam(TeamId teamId, string? name = null, string? description = null, Dictionary<string, string>? settings = null)
    {
        var team = _queries.GetTeam(teamId) ?? throw new InvalidOperationException("Team not found");
        var currentUser = GetCurrentUser();

        // Check permission
        var membership = _queries.GetMembership(teamId, currentUser.Id);
        if (membership is null || !membership.Role.CanManageTeam())
            throw new UnauthorizedAccessException("Insufficient permissions");

        var updated = team with
        {
            Name = name ?? team.Name,
            Description = description ?? team.Description,
            Settings = settings ?? team.Settings,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _queries.UpdateTeam(updated);

        return updated;
    }

    public void DeleteTeam(TeamId teamId)
    {
        var team = _queries.GetTeam(teamId) ?? throw new InvalidOperationException("Team not found");
        var currentUser = GetCurrentUser();

        // Only owner can delete
        if (team.OwnerId != currentUser.Id)
            throw new UnauthorizedAccessException("Only owner can delete team");

        _queries.DeleteTeam(teamId);

        LogActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.TeamDeleted,
            $"Team '{team.Name}' deleted",
            teamId, null, null, null,
            DateTimeOffset.UtcNow,
            null
        ));
    }

    // ========================================================================
    // Membership Operations
    // ========================================================================

    public TeamMembership AddMember(TeamId teamId, UserId userId, TeamRole role = TeamRole.Member)
    {
        var currentUser = GetCurrentUser();
        var user = _queries.GetUser(userId) ?? throw new InvalidOperationException("User not found");

        var membership = new TeamMembership(
            TeamMembershipId.New(),
            teamId,
            userId,
            role,
            currentUser.Id,
            DateTimeOffset.UtcNow
        );

        _queries.InsertMembership(membership);

        LogActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.MemberAdded,
            $"User {user.Username} added to team",
            teamId, null, userId, null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object> { ["role"] = role.ToString() }
        ));

        MemberAdded?.Invoke(this, new MemberAddedEventArgs { TeamId = teamId, UserId = userId, Role = role });

        return membership;
    }

    public TeamMembership UpdateMemberRole(TeamId teamId, UserId userId, TeamRole role)
    {
        var currentUser = GetCurrentUser();
        var myMembership = _queries.GetMembership(teamId, currentUser.Id);

        if (myMembership is null || !myMembership.Role.CanManageTeam())
            throw new UnauthorizedAccessException("Insufficient permissions");

        _queries.UpdateMembershipRole(teamId, userId, role);

        LogActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.RoleChanged,
            $"User role changed to {role}",
            teamId, null, userId, null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object> { ["newRole"] = role.ToString() }
        ));

        var updated = _queries.GetMembership(teamId, userId);
        return updated!;
    }

    public void RemoveMember(TeamId teamId, UserId userId)
    {
        var currentUser = GetCurrentUser();
        var team = _queries.GetTeam(teamId) ?? throw new InvalidOperationException("Team not found");

        // Can't remove owner
        if (team.OwnerId == userId)
            throw new InvalidOperationException("Cannot remove team owner");

        // Check permission (can remove self or if admin)
        if (userId != currentUser.Id)
        {
            var membership = _queries.GetMembership(teamId, currentUser.Id);
            if (membership is null || !membership.Role.CanManageTeam())
                throw new UnauthorizedAccessException("Insufficient permissions");
        }

        _queries.DeleteMembership(teamId, userId);

        LogActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.MemberRemoved,
            "User removed from team",
            teamId, null, userId, null,
            DateTimeOffset.UtcNow,
            null
        ));

        MemberRemoved?.Invoke(this, new MemberRemovedEventArgs { TeamId = teamId, UserId = userId });
    }

    public IReadOnlyList<TeamMembership> GetTeamMembers(TeamId teamId) => _queries.GetTeamMemberships(teamId);

    public TeamMembership? GetMembership(TeamId teamId, UserId userId) => _queries.GetMembership(teamId, userId);

    // ========================================================================
    // Invitation Operations
    // ========================================================================

    public TeamInvitation InviteUser(TeamId teamId, string email, TeamRole role = TeamRole.Member)
    {
        var currentUser = GetCurrentUser();
        var team = _queries.GetTeam(teamId) ?? throw new InvalidOperationException("Team not found");

        var membership = _queries.GetMembership(teamId, currentUser.Id);
        if (membership is null || !membership.Role.CanManageTeam())
            throw new UnauthorizedAccessException("Insufficient permissions");

        // Check if user exists
        var existingUser = _queries.GetUserByEmail(email);

        var invitation = new TeamInvitation(
            TeamInvitationId.New(),
            teamId,
            email,
            existingUser?.Id,
            role,
            currentUser.Id,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7),
            InvitationStatus.Pending
        );

        _queries.InsertInvitation(invitation);

        // Send notification if user exists
        if (existingUser is not null)
        {
            SendNotification(new Notification(
                NotificationId.New(),
                existingUser.Id,
                NotificationType.TeamInvitation,
                $"You've been invited to join {team.Name}",
                false,
                teamId,
                null,
                null,
                DateTimeOffset.UtcNow,
                null,
                new Dictionary<string, object> { ["invitedBy"] = currentUser.DisplayName }
            ));
        }

        InvitationSent?.Invoke(this, new InvitationSentEventArgs
        {
            TeamId = teamId,
            Email = email,
            UserId = existingUser?.Id,
            Role = role
        });

        return invitation;
    }

    public TeamInvitation InviteByUsername(TeamId teamId, string username, TeamRole role = TeamRole.Member)
    {
        var user = _queries.GetUserByUsername(username) ?? throw new InvalidOperationException("User not found");

        // Check if already a member
        var existing = _queries.GetMembership(teamId, user.Id);
        if (existing is not null)
            throw new InvalidOperationException("User is already a team member");

        return InviteUser(teamId, user.Email, role);
    }

    public TeamInvitation? GetInvitation(TeamInvitationId invitationId) => _queries.GetInvitation(invitationId);

    public IReadOnlyList<TeamInvitation> GetPendingInvitations(TeamId teamId) => _queries.GetPendingInvitations(teamId);

    public IReadOnlyList<TeamInvitation> GetUserInvitations()
    {
        var currentUser = GetCurrentUser();
        return _queries.GetUserInvitations(currentUser.Id, currentUser.Email);
    }

    public void AcceptInvitation(TeamInvitationId invitationId)
    {
        var invitation = _queries.GetInvitation(invitationId) ?? throw new InvalidOperationException("Invitation not found");
        var currentUser = GetCurrentUser();

        if (invitation.Status != InvitationStatus.Pending)
            throw new InvalidOperationException("Invitation is no longer pending");

        if (invitation.ExpiresAt < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Invitation has expired");

        _queries.UpdateInvitationStatus(invitationId, InvitationStatus.Accepted);

        AddMember(invitation.TeamId, currentUser.Id, invitation.Role);
    }

    public void DeclineInvitation(TeamInvitationId invitationId)
    {
        var invitation = _queries.GetInvitation(invitationId) ?? throw new InvalidOperationException("Invitation not found");

        _queries.UpdateInvitationStatus(invitationId, InvitationStatus.Declined);
    }

    public void CancelInvitation(TeamInvitationId invitationId)
    {
        var invitation = _queries.GetInvitation(invitationId) ?? throw new InvalidOperationException("Invitation not found");
        var currentUser = GetCurrentUser();

        var membership = _queries.GetMembership(invitation.TeamId, currentUser.Id);
        if (membership is null || !membership.Role.CanManageTeam())
            throw new UnauthorizedAccessException("Insufficient permissions");

        _queries.UpdateInvitationStatus(invitationId, InvitationStatus.Cancelled);
    }

    // ========================================================================
    // Activity & Notification Helpers
    // ========================================================================

    private void LogActivity(ActivityEntry entry)
    {
        _queries.InsertActivity(entry);
    }

    private void SendNotification(Notification notification)
    {
        _queries.InsertNotification(notification);
    }
}

// Event Args
public sealed class TeamCreatedEventArgs : EventArgs
{
    public required Team Team { get; init; }
    public required User Creator { get; init; }
}

public sealed class MemberAddedEventArgs : EventArgs
{
    public required TeamId TeamId { get; init; }
    public required UserId UserId { get; init; }
    public required TeamRole Role { get; init; }
}

public sealed class MemberRemovedEventArgs : EventArgs
{
    public required TeamId TeamId { get; init; }
    public required UserId UserId { get; init; }
}

public sealed class InvitationSentEventArgs : EventArgs
{
    public required TeamId TeamId { get; init; }
    public required string Email { get; init; }
    public UserId? UserId { get; init; }
    public required TeamRole Role { get; init; }
}
