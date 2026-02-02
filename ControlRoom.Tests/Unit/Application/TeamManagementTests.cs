using ControlRoom.Domain.Model;
using ControlRoom.Application.UseCases;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Unit tests for TeamManagement and ResourceSharing use cases.
/// </summary>
public sealed class TeamManagementTests
{
    // ========================================================================
    // Event Args Tests
    // ========================================================================

    [Fact]
    public void TeamCreatedEventArgs_Properties()
    {
        var team = new Team(
            TeamId.New(),
            "Test Team",
            "Description",
            UserId.New(),
            DateTimeOffset.UtcNow,
            null,
            new Dictionary<string, string>(),
            new List<TeamMembership>()
        );

        var user = new User(
            UserId.New(),
            "testuser",
            "Test User",
            "test@test.com",
            UserRole.User,
            DateTimeOffset.UtcNow,
            null,
            new Dictionary<string, string>()
        );

        var args = new TeamCreatedEventArgs { Team = team, Creator = user };

        Assert.Equal(team, args.Team);
        Assert.Equal(user, args.Creator);
    }

    [Fact]
    public void MemberAddedEventArgs_Properties()
    {
        var teamId = TeamId.New();
        var userId = UserId.New();

        var args = new MemberAddedEventArgs
        {
            TeamId = teamId,
            UserId = userId,
            Role = TeamRole.Member
        };

        Assert.Equal(teamId, args.TeamId);
        Assert.Equal(userId, args.UserId);
        Assert.Equal(TeamRole.Member, args.Role);
    }

    [Fact]
    public void MemberRemovedEventArgs_Properties()
    {
        var teamId = TeamId.New();
        var userId = UserId.New();

        var args = new MemberRemovedEventArgs
        {
            TeamId = teamId,
            UserId = userId
        };

        Assert.Equal(teamId, args.TeamId);
        Assert.Equal(userId, args.UserId);
    }

    [Fact]
    public void InvitationSentEventArgs_WithUserId()
    {
        var teamId = TeamId.New();
        var userId = UserId.New();

        var args = new InvitationSentEventArgs
        {
            TeamId = teamId,
            Email = "test@test.com",
            UserId = userId,
            Role = TeamRole.Admin
        };

        Assert.Equal(teamId, args.TeamId);
        Assert.Equal("test@test.com", args.Email);
        Assert.Equal(userId, args.UserId);
        Assert.Equal(TeamRole.Admin, args.Role);
    }

    [Fact]
    public void InvitationSentEventArgs_WithoutUserId()
    {
        var teamId = TeamId.New();

        var args = new InvitationSentEventArgs
        {
            TeamId = teamId,
            Email = "newuser@test.com",
            Role = TeamRole.Member
        };

        Assert.Equal(teamId, args.TeamId);
        Assert.Equal("newuser@test.com", args.Email);
        Assert.Null(args.UserId);
        Assert.Equal(TeamRole.Member, args.Role);
    }

    [Fact]
    public void ResourceSharedEventArgs_WithTeam()
    {
        var teamId = TeamId.New();
        var resource = new SharedResource(
            SharedResourceId.New(),
            ResourceType.Script,
            Guid.NewGuid(),
            UserId.New(),
            teamId,
            null,
            DateTimeOffset.UtcNow,
            new List<ResourcePermission>()
        );

        var args = new ResourceSharedEventArgs
        {
            Resource = resource,
            TeamId = teamId,
            Permission = PermissionLevel.Edit
        };

        Assert.Equal(resource, args.Resource);
        Assert.Equal(teamId, args.TeamId);
        Assert.Null(args.UserId);
        Assert.Equal(PermissionLevel.Edit, args.Permission);
    }

    [Fact]
    public void ResourceSharedEventArgs_WithUser()
    {
        var userId = UserId.New();
        var resource = new SharedResource(
            SharedResourceId.New(),
            ResourceType.Runbook,
            Guid.NewGuid(),
            UserId.New(),
            null,
            userId,
            DateTimeOffset.UtcNow,
            new List<ResourcePermission>()
        );

        var args = new ResourceSharedEventArgs
        {
            Resource = resource,
            UserId = userId,
            Permission = PermissionLevel.View
        };

        Assert.Equal(resource, args.Resource);
        Assert.Null(args.TeamId);
        Assert.Equal(userId, args.UserId);
        Assert.Equal(PermissionLevel.View, args.Permission);
    }

    [Fact]
    public void PermissionChangedEventArgs_Properties()
    {
        var resourceId = SharedResourceId.New();
        var userId = UserId.New();

        var args = new PermissionChangedEventArgs
        {
            ResourceId = resourceId,
            UserId = userId,
            NewPermission = PermissionLevel.Execute
        };

        Assert.Equal(resourceId, args.ResourceId);
        Assert.Equal(userId, args.UserId);
        Assert.Equal(PermissionLevel.Execute, args.NewPermission);
    }

    [Fact]
    public void AccessRevokedEventArgs_Properties()
    {
        var resourceId = SharedResourceId.New();
        var userId = UserId.New();

        var args = new AccessRevokedEventArgs
        {
            ResourceId = resourceId,
            UserId = userId
        };

        Assert.Equal(resourceId, args.ResourceId);
        Assert.Equal(userId, args.UserId);
    }

    // ========================================================================
    // Domain Model Integration Tests
    // ========================================================================

    [Fact]
    public void User_WithPreferences()
    {
        var prefs = new Dictionary<string, string>
        {
            ["theme"] = "dark",
            ["notifications"] = "enabled",
            ["language"] = "en-US"
        };

        var user = new User(
            UserId.New(),
            "testuser",
            "Test User",
            "test@example.com",
            UserRole.User,
            DateTimeOffset.UtcNow,
            null,
            prefs
        );

        Assert.Equal(3, user.Preferences.Count);
        Assert.Equal("dark", user.Preferences["theme"]);
        Assert.Equal("enabled", user.Preferences["notifications"]);
    }

    [Fact]
    public void Team_WithSettings()
    {
        var settings = new Dictionary<string, string>
        {
            ["defaultPermission"] = "View",
            ["allowExternalInvites"] = "false"
        };

        var team = new Team(
            TeamId.New(),
            "Engineering",
            "Engineering team",
            UserId.New(),
            DateTimeOffset.UtcNow,
            null,
            settings,
            new List<TeamMembership>()
        );

        Assert.Equal(2, team.Settings.Count);
        Assert.Equal("View", team.Settings["defaultPermission"]);
    }

    [Fact]
    public void TeamMembership_AllRoles()
    {
        var teamId = TeamId.New();
        var addedBy = UserId.New();

        var viewerMembership = new TeamMembership(
            TeamMembershipId.New(),
            teamId,
            UserId.New(),
            TeamRole.Viewer,
            addedBy,
            DateTimeOffset.UtcNow
        );

        var memberMembership = new TeamMembership(
            TeamMembershipId.New(),
            teamId,
            UserId.New(),
            TeamRole.Member,
            addedBy,
            DateTimeOffset.UtcNow
        );

        var adminMembership = new TeamMembership(
            TeamMembershipId.New(),
            teamId,
            UserId.New(),
            TeamRole.Admin,
            addedBy,
            DateTimeOffset.UtcNow
        );

        var ownerMembership = new TeamMembership(
            TeamMembershipId.New(),
            teamId,
            UserId.New(),
            TeamRole.Owner,
            addedBy,
            DateTimeOffset.UtcNow
        );

        Assert.Equal(TeamRole.Viewer, viewerMembership.Role);
        Assert.Equal(TeamRole.Member, memberMembership.Role);
        Assert.Equal(TeamRole.Admin, adminMembership.Role);
        Assert.Equal(TeamRole.Owner, ownerMembership.Role);
    }

    [Fact]
    public void SharedResource_WithMultiplePermissions()
    {
        var ownerId = UserId.New();
        var user1 = UserId.New();
        var user2 = UserId.New();
        var user3 = UserId.New();

        var permissions = new List<ResourcePermission>
        {
            new(ownerId, PermissionLevel.Admin, DateTimeOffset.UtcNow, ownerId),
            new(user1, PermissionLevel.Edit, DateTimeOffset.UtcNow, ownerId),
            new(user2, PermissionLevel.Execute, DateTimeOffset.UtcNow, ownerId),
            new(user3, PermissionLevel.View, DateTimeOffset.UtcNow, ownerId)
        };

        var shared = new SharedResource(
            SharedResourceId.New(),
            ResourceType.Dashboard,
            Guid.NewGuid(),
            ownerId,
            TeamId.New(),
            null,
            DateTimeOffset.UtcNow,
            permissions
        );

        Assert.Equal(4, shared.Permissions.Count);

        var ownerPerm = shared.Permissions.First(p => p.UserId == ownerId);
        Assert.Equal(PermissionLevel.Admin, ownerPerm.Level);
        Assert.True(ownerPerm.Level.CanAdmin());

        var editPerm = shared.Permissions.First(p => p.UserId == user1);
        Assert.Equal(PermissionLevel.Edit, editPerm.Level);
        Assert.True(editPerm.Level.CanEdit());
        Assert.False(editPerm.Level.CanAdmin());

        var executePerm = shared.Permissions.First(p => p.UserId == user2);
        Assert.Equal(PermissionLevel.Execute, executePerm.Level);
        Assert.True(executePerm.Level.CanExecute());
        Assert.False(executePerm.Level.CanEdit());

        var viewPerm = shared.Permissions.First(p => p.UserId == user3);
        Assert.Equal(PermissionLevel.View, viewPerm.Level);
        Assert.True(viewPerm.Level.CanView());
        Assert.False(viewPerm.Level.CanExecute());
    }

    [Fact]
    public void TeamInvitation_ExpirationCheck()
    {
        var validInvitation = new TeamInvitation(
            TeamInvitationId.New(),
            TeamId.New(),
            "test@test.com",
            null,
            TeamRole.Member,
            UserId.New(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7),
            InvitationStatus.Pending
        );

        var expiredInvitation = new TeamInvitation(
            TeamInvitationId.New(),
            TeamId.New(),
            "expired@test.com",
            null,
            TeamRole.Member,
            UserId.New(),
            DateTimeOffset.UtcNow.AddDays(-14),
            DateTimeOffset.UtcNow.AddDays(-7),
            InvitationStatus.Pending
        );

        Assert.True(validInvitation.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(expiredInvitation.ExpiresAt < DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ActivityEntry_WithMetadata()
    {
        var metadata = new Dictionary<string, object>
        {
            ["previousRole"] = "Member",
            ["newRole"] = "Admin",
            ["changedBy"] = "system"
        };

        var entry = new ActivityEntry(
            ActivityId.New(),
            UserId.New(),
            ActivityType.RoleChanged,
            "Role changed from Member to Admin",
            TeamId.New(),
            null,
            UserId.New(),
            null,
            DateTimeOffset.UtcNow,
            metadata
        );

        Assert.NotNull(entry.Metadata);
        Assert.Equal(3, entry.Metadata.Count);
        Assert.Equal("Member", entry.Metadata["previousRole"]);
        Assert.Equal("Admin", entry.Metadata["newRole"]);
    }

    [Fact]
    public void Notification_ReadUnreadState()
    {
        var unreadNotification = new Notification(
            NotificationId.New(),
            UserId.New(),
            NotificationType.TeamInvitation,
            "You've been invited",
            false,
            TeamId.New(),
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            null
        );

        Assert.False(unreadNotification.IsRead);
        Assert.Null(unreadNotification.ReadAt);

        var readNotification = unreadNotification with
        {
            IsRead = true,
            ReadAt = DateTimeOffset.UtcNow
        };

        Assert.True(readNotification.IsRead);
        Assert.NotNull(readNotification.ReadAt);
    }

    [Fact]
    public void Comment_ReplyChain()
    {
        var parentComment = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "This is the parent comment",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        var replyComment = new Comment(
            CommentId.New(),
            parentComment.ResourceId,
            UserId.New(),
            "This is a reply",
            DateTimeOffset.UtcNow,
            null,
            parentComment.Id,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        Assert.Null(parentComment.ParentId);
        Assert.Equal(parentComment.Id, replyComment.ParentId);
    }

    [Fact]
    public void Annotation_PositionAndLength()
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Highlight,
            "Important code section",
            100,
            50,
            DateTimeOffset.UtcNow,
            null,
            "#FFFF00",
            null
        );

        Assert.Equal(100, annotation.StartPosition);
        Assert.Equal(50, annotation.Length);

        // Calculated end position would be 150
        var endPosition = annotation.StartPosition + annotation.Length;
        Assert.Equal(150, endPosition);
    }

    [Theory]
    [InlineData(ResourceType.Script)]
    [InlineData(ResourceType.Runbook)]
    [InlineData(ResourceType.Dashboard)]
    [InlineData(ResourceType.Alert)]
    [InlineData(ResourceType.HealthCheck)]
    [InlineData(ResourceType.SelfHealingRule)]
    public void SharedResource_AllResourceTypes(ResourceType resourceType)
    {
        var shared = new SharedResource(
            SharedResourceId.New(),
            resourceType,
            Guid.NewGuid(),
            UserId.New(),
            TeamId.New(),
            null,
            DateTimeOffset.UtcNow,
            new List<ResourcePermission>()
        );

        Assert.Equal(resourceType, shared.ResourceType);
    }

    [Fact]
    public void Team_MembersCollection()
    {
        var teamId = TeamId.New();
        var ownerId = UserId.New();

        var members = new List<TeamMembership>
        {
            new(TeamMembershipId.New(), teamId, ownerId, TeamRole.Owner, ownerId, DateTimeOffset.UtcNow),
            new(TeamMembershipId.New(), teamId, UserId.New(), TeamRole.Admin, ownerId, DateTimeOffset.UtcNow),
            new(TeamMembershipId.New(), teamId, UserId.New(), TeamRole.Member, ownerId, DateTimeOffset.UtcNow),
            new(TeamMembershipId.New(), teamId, UserId.New(), TeamRole.Viewer, ownerId, DateTimeOffset.UtcNow)
        };

        var team = new Team(
            teamId,
            "Full Team",
            "A team with all role types",
            ownerId,
            DateTimeOffset.UtcNow,
            null,
            new Dictionary<string, string>(),
            members
        );

        Assert.Equal(4, team.Members.Count);
        Assert.Single(team.Members.Where(m => m.Role == TeamRole.Owner));
        Assert.Single(team.Members.Where(m => m.Role == TeamRole.Admin));
        Assert.Single(team.Members.Where(m => m.Role == TeamRole.Member));
        Assert.Single(team.Members.Where(m => m.Role == TeamRole.Viewer));
    }
}
