using ControlRoom.Domain.Model;
using ControlRoom.Application.UseCases;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Unit tests for Collaboration (Comments & Annotations) use case.
/// </summary>
public sealed class CollaborationTests
{
    // ========================================================================
    // Comment Event Args Tests
    // ========================================================================

    [Fact]
    public void CommentAddedEventArgs_Properties()
    {
        var comment = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "This is a test comment",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        var args = new CommentAddedEventArgs { Comment = comment };

        Assert.Equal(comment, args.Comment);
        Assert.Equal("This is a test comment", args.Comment.Content);
    }

    [Fact]
    public void CommentEditedEventArgs_Properties()
    {
        var comment = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "Updated content",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        var args = new CommentEditedEventArgs
        {
            Comment = comment,
            PreviousContent = "Original content"
        };

        Assert.Equal(comment, args.Comment);
        Assert.Equal("Updated content", args.Comment.Content);
        Assert.Equal("Original content", args.PreviousContent);
    }

    [Fact]
    public void CommentDeletedEventArgs_Properties()
    {
        var commentId = CommentId.New();
        var resourceId = SharedResourceId.New();

        var args = new CommentDeletedEventArgs
        {
            CommentId = commentId,
            ResourceId = resourceId
        };

        Assert.Equal(commentId, args.CommentId);
        Assert.Equal(resourceId, args.ResourceId);
    }

    [Fact]
    public void ReactionAddedEventArgs_Properties()
    {
        var commentId = CommentId.New();

        var args = new ReactionAddedEventArgs
        {
            CommentId = commentId,
            Emoji = "\U0001F44D"
        };

        Assert.Equal(commentId, args.CommentId);
        Assert.Equal("\U0001F44D", args.Emoji);
    }

    // ========================================================================
    // Annotation Event Args Tests
    // ========================================================================

    [Fact]
    public void AnnotationAddedEventArgs_Properties()
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Highlight,
            "Important",
            100,
            50,
            DateTimeOffset.UtcNow,
            null,
            "#FFFF00",
            null
        );

        var args = new AnnotationAddedEventArgs { Annotation = annotation };

        Assert.Equal(annotation, args.Annotation);
        Assert.Equal(AnnotationType.Highlight, args.Annotation.Type);
    }

    [Fact]
    public void AnnotationDeletedEventArgs_Properties()
    {
        var annotationId = AnnotationId.New();
        var resourceId = SharedResourceId.New();

        var args = new AnnotationDeletedEventArgs
        {
            AnnotationId = annotationId,
            ResourceId = resourceId
        };

        Assert.Equal(annotationId, args.AnnotationId);
        Assert.Equal(resourceId, args.ResourceId);
    }

    // ========================================================================
    // Comment Domain Model Tests
    // ========================================================================

    [Fact]
    public void Comment_BasicConstruction()
    {
        var commentId = CommentId.New();
        var resourceId = SharedResourceId.New();
        var authorId = UserId.New();

        var comment = new Comment(
            commentId,
            resourceId,
            authorId,
            "This is my comment",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        Assert.Equal(commentId, comment.Id);
        Assert.Equal(resourceId, comment.ResourceId);
        Assert.Equal(authorId, comment.AuthorId);
        Assert.Equal("This is my comment", comment.Content);
        Assert.Null(comment.EditedAt);
        Assert.Null(comment.ParentId);
        Assert.Empty(comment.Mentions);
        Assert.Empty(comment.Reactions);
    }

    [Fact]
    public void Comment_WithMentions()
    {
        var user1 = UserId.New();
        var user2 = UserId.New();
        var user3 = UserId.New();

        var comment = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "Hey @user1, @user2, and @user3!",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId> { user1, user2, user3 },
            new Dictionary<string, int>()
        );

        Assert.Equal(3, comment.Mentions.Count);
        Assert.Contains(user1, comment.Mentions);
        Assert.Contains(user2, comment.Mentions);
        Assert.Contains(user3, comment.Mentions);
    }

    [Fact]
    public void Comment_WithReactions()
    {
        var reactions = new Dictionary<string, int>
        {
            ["\U0001F44D"] = 5,   // thumbs up
            ["\u2764\uFE0F"] = 3,     // heart
            ["\U0001F389"] = 2,   // party
            ["\U0001F914"] = 1    // thinking
        };

        var comment = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "Great work!",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            reactions
        );

        Assert.Equal(4, comment.Reactions.Count);
        Assert.Equal(5, comment.Reactions["\U0001F44D"]);
        Assert.Equal(3, comment.Reactions["\u2764\uFE0F"]);
        Assert.Equal(2, comment.Reactions["\U0001F389"]);
        Assert.Equal(1, comment.Reactions["\U0001F914"]);
    }

    [Fact]
    public void Comment_Reply()
    {
        var parentId = CommentId.New();
        var resourceId = SharedResourceId.New();

        var parentComment = new Comment(
            parentId,
            resourceId,
            UserId.New(),
            "Parent comment",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        var replyComment = new Comment(
            CommentId.New(),
            resourceId,
            UserId.New(),
            "This is a reply",
            DateTimeOffset.UtcNow,
            null,
            parentId,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        Assert.Null(parentComment.ParentId);
        Assert.Equal(parentId, replyComment.ParentId);
        Assert.Equal(resourceId, parentComment.ResourceId);
        Assert.Equal(resourceId, replyComment.ResourceId);
    }

    [Fact]
    public void Comment_Edited()
    {
        var original = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "Original content",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            null,
            null,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        Assert.Null(original.EditedAt);

        var edited = original with
        {
            Content = "Edited content",
            EditedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("Edited content", edited.Content);
        Assert.NotNull(edited.EditedAt);
        Assert.True(edited.EditedAt > original.CreatedAt);
    }

    // ========================================================================
    // Annotation Domain Model Tests
    // ========================================================================

    [Fact]
    public void Annotation_Highlight()
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Highlight,
            "Important section",
            100,
            50,
            DateTimeOffset.UtcNow,
            null,
            "#FFFF00",
            null
        );

        Assert.Equal(AnnotationType.Highlight, annotation.Type);
        Assert.Equal(100, annotation.StartPosition);
        Assert.Equal(50, annotation.Length);
        Assert.Equal("#FFFF00", annotation.Color);
    }

    [Fact]
    public void Annotation_Comment()
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Comment,
            "This section needs review",
            200,
            30,
            DateTimeOffset.UtcNow,
            null,
            null,
            null
        );

        Assert.Equal(AnnotationType.Comment, annotation.Type);
        Assert.Equal("This section needs review", annotation.Content);
    }

    [Fact]
    public void Annotation_Bookmark()
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Bookmark,
            "Return to this later",
            0,
            0,
            DateTimeOffset.UtcNow,
            null,
            null,
            new Dictionary<string, object> { ["priority"] = "high" }
        );

        Assert.Equal(AnnotationType.Bookmark, annotation.Type);
        Assert.NotNull(annotation.Metadata);
        Assert.Equal("high", annotation.Metadata["priority"]);
    }

    [Fact]
    public void Annotation_Warning()
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Warning,
            "Potential security issue",
            500,
            100,
            DateTimeOffset.UtcNow,
            null,
            "#FF0000",
            new Dictionary<string, object>
            {
                ["severity"] = "high",
                ["category"] = "security"
            }
        );

        Assert.Equal(AnnotationType.Warning, annotation.Type);
        Assert.Equal("#FF0000", annotation.Color);
        Assert.Equal("high", annotation.Metadata!["severity"]);
        Assert.Equal("security", annotation.Metadata["category"]);
    }

    [Fact]
    public void Annotation_Todo()
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Todo,
            "Refactor this function",
            300,
            80,
            DateTimeOffset.UtcNow,
            null,
            "#FFA500",
            new Dictionary<string, object>
            {
                ["completed"] = false,
                ["assignee"] = "developer1"
            }
        );

        Assert.Equal(AnnotationType.Todo, annotation.Type);
        Assert.False((bool)annotation.Metadata!["completed"]);
    }

    [Fact]
    public void Annotation_Updated()
    {
        var original = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Highlight,
            "Original",
            100,
            50,
            DateTimeOffset.UtcNow.AddHours(-1),
            null,
            "#FFFF00",
            null
        );

        Assert.Null(original.UpdatedAt);

        var updated = original with
        {
            Content = "Updated content",
            Color = "#00FF00",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("Updated content", updated.Content);
        Assert.Equal("#00FF00", updated.Color);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Theory]
    [InlineData(AnnotationType.Highlight)]
    [InlineData(AnnotationType.Comment)]
    [InlineData(AnnotationType.Bookmark)]
    [InlineData(AnnotationType.Warning)]
    [InlineData(AnnotationType.Todo)]
    public void Annotation_AllTypes(AnnotationType type)
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            type,
            "Test",
            0,
            10,
            DateTimeOffset.UtcNow,
            null,
            null,
            null
        );

        Assert.Equal(type, annotation.Type);
    }

    [Fact]
    public void Annotation_PositionCalculations()
    {
        var annotation = new Annotation(
            AnnotationId.New(),
            SharedResourceId.New(),
            UserId.New(),
            AnnotationType.Highlight,
            "Test",
            100,
            50,
            DateTimeOffset.UtcNow,
            null,
            null,
            null
        );

        // Start position
        Assert.Equal(100, annotation.StartPosition);

        // End position
        var endPosition = annotation.StartPosition + annotation.Length;
        Assert.Equal(150, endPosition);

        // Length
        Assert.Equal(50, annotation.Length);
    }

    // ========================================================================
    // Comment Thread Tests
    // ========================================================================

    [Fact]
    public void Comment_ThreadStructure()
    {
        var resourceId = SharedResourceId.New();
        var authorId = UserId.New();

        // Root comment
        var rootComment = new Comment(
            CommentId.New(),
            resourceId,
            authorId,
            "Root comment",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        // First level replies
        var reply1 = new Comment(
            CommentId.New(),
            resourceId,
            UserId.New(),
            "Reply 1",
            DateTimeOffset.UtcNow,
            null,
            rootComment.Id,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        var reply2 = new Comment(
            CommentId.New(),
            resourceId,
            UserId.New(),
            "Reply 2",
            DateTimeOffset.UtcNow,
            null,
            rootComment.Id,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        // Nested reply
        var nestedReply = new Comment(
            CommentId.New(),
            resourceId,
            UserId.New(),
            "Nested reply to reply 1",
            DateTimeOffset.UtcNow,
            null,
            reply1.Id,
            new List<UserId>(),
            new Dictionary<string, int>()
        );

        // Verify structure
        Assert.Null(rootComment.ParentId);
        Assert.Equal(rootComment.Id, reply1.ParentId);
        Assert.Equal(rootComment.Id, reply2.ParentId);
        Assert.Equal(reply1.Id, nestedReply.ParentId);

        // All belong to same resource
        Assert.Equal(resourceId, rootComment.ResourceId);
        Assert.Equal(resourceId, reply1.ResourceId);
        Assert.Equal(resourceId, reply2.ResourceId);
        Assert.Equal(resourceId, nestedReply.ResourceId);
    }

    // ========================================================================
    // Reaction Aggregation Tests
    // ========================================================================

    [Fact]
    public void Reaction_Increment()
    {
        var reactions = new Dictionary<string, int>
        {
            ["\U0001F44D"] = 5
        };

        var comment = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "Test",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            reactions
        );

        Assert.Equal(5, comment.Reactions["\U0001F44D"]);

        // Simulate adding another reaction
        var updatedReactions = new Dictionary<string, int>(reactions)
        {
            ["\U0001F44D"] = reactions["\U0001F44D"] + 1
        };

        var updated = comment with { Reactions = updatedReactions };
        Assert.Equal(6, updated.Reactions["\U0001F44D"]);
    }

    [Fact]
    public void Reaction_NewEmoji()
    {
        var reactions = new Dictionary<string, int>
        {
            ["\U0001F44D"] = 5
        };

        var comment = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "Test",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            reactions
        );

        Assert.Single(comment.Reactions);

        // Add new emoji
        var updatedReactions = new Dictionary<string, int>(reactions)
        {
            ["\u2764\uFE0F"] = 1
        };

        var updated = comment with { Reactions = updatedReactions };
        Assert.Equal(2, updated.Reactions.Count);
        Assert.Equal(1, updated.Reactions["\u2764\uFE0F"]);
    }

    [Fact]
    public void Reaction_Remove()
    {
        var reactions = new Dictionary<string, int>
        {
            ["\U0001F44D"] = 1,
            ["\u2764\uFE0F"] = 2
        };

        var comment = new Comment(
            CommentId.New(),
            SharedResourceId.New(),
            UserId.New(),
            "Test",
            DateTimeOffset.UtcNow,
            null,
            null,
            new List<UserId>(),
            reactions
        );

        // Remove thumbs up (count goes to 0)
        var updatedReactions = new Dictionary<string, int>
        {
            ["\u2764\uFE0F"] = 2
        };

        var updated = comment with { Reactions = updatedReactions };
        Assert.Single(updated.Reactions);
        Assert.False(updated.Reactions.ContainsKey("\U0001F44D"));
    }
}
