using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.UseCases;

/// <summary>
/// Use case for comments and annotations on shared resources.
/// </summary>
public sealed class Collaboration
{
    private readonly Db _db;
    private readonly TeamQueries _queries;
    private readonly TeamManagement _teamManagement;

    public Collaboration(Db db, TeamManagement teamManagement)
    {
        _db = db;
        _queries = new TeamQueries(db);
        _teamManagement = teamManagement;
    }

    // Events
    public event EventHandler<CommentAddedEventArgs>? CommentAdded;
    public event EventHandler<CommentEditedEventArgs>? CommentEdited;
    public event EventHandler<CommentDeletedEventArgs>? CommentDeleted;
    public event EventHandler<ReactionAddedEventArgs>? ReactionAdded;
    public event EventHandler<AnnotationAddedEventArgs>? AnnotationAdded;
    public event EventHandler<AnnotationDeletedEventArgs>? AnnotationDeleted;

    // ========================================================================
    // Comment Operations
    // ========================================================================

    public Comment AddComment(SharedResourceId resourceId, string content, CommentId? parentId = null)
    {
        var currentUser = _teamManagement.GetCurrentUser();

        // Parse mentions from content (e.g., @username)
        var mentions = ParseMentions(content);

        var comment = new Comment(
            CommentId.New(),
            resourceId,
            currentUser.Id,
            content,
            DateTimeOffset.UtcNow,
            null,
            parentId,
            mentions,
            new Dictionary<string, int>()
        );

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, """
            INSERT INTO comments (id, resource_id, author_id, content, created_at, edited_at, parent_id, mentions, reactions)
            VALUES ($id, $resource_id, $author_id, $content, $created_at, $edited_at, $parent_id, $mentions, $reactions)
            """,
            ("$id", comment.Id.ToString()),
            ("$resource_id", resourceId.ToString()),
            ("$author_id", currentUser.Id.ToString()),
            ("$content", content),
            ("$created_at", comment.CreatedAt.ToString("O")),
            ("$edited_at", DBNull.Value),
            ("$parent_id", parentId?.ToString() ?? (object)DBNull.Value),
            ("$mentions", JsonSerializer.Serialize(mentions.Select(m => m.ToString()))),
            ("$reactions", "{}"));

        tx.Commit();

        // Log activity
        _queries.InsertActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.CommentAdded,
            "Comment added",
            null,
            resourceId,
            null,
            comment.Id,
            DateTimeOffset.UtcNow,
            null
        ));

        // Send notifications for mentions
        foreach (var mentionedUserId in mentions)
        {
            _queries.InsertNotification(new Notification(
                NotificationId.New(),
                mentionedUserId,
                NotificationType.MentionedInComment,
                $"{currentUser.DisplayName} mentioned you in a comment",
                false,
                null,
                resourceId,
                comment.Id,
                DateTimeOffset.UtcNow,
                null,
                null
            ));
        }

        // Send notification for reply
        if (parentId.HasValue)
        {
            var parentComment = GetComment(parentId.Value);
            if (parentComment is not null && parentComment.AuthorId != currentUser.Id)
            {
                _queries.InsertNotification(new Notification(
                    NotificationId.New(),
                    parentComment.AuthorId,
                    NotificationType.CommentReply,
                    $"{currentUser.DisplayName} replied to your comment",
                    false,
                    null,
                    resourceId,
                    comment.Id,
                    DateTimeOffset.UtcNow,
                    null,
                    null
                ));
            }
        }

        CommentAdded?.Invoke(this, new CommentAddedEventArgs { Comment = comment });

        return comment;
    }

    public Comment EditComment(CommentId commentId, string newContent)
    {
        var currentUser = _teamManagement.GetCurrentUser();
        var comment = GetComment(commentId) ?? throw new InvalidOperationException("Comment not found");

        if (comment.AuthorId != currentUser.Id)
            throw new UnauthorizedAccessException("Can only edit your own comments");

        var mentions = ParseMentions(newContent);

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE comments SET content = $content, edited_at = $edited_at, mentions = $mentions
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", commentId.ToString());
        cmd.Parameters.AddWithValue("$content", newContent);
        cmd.Parameters.AddWithValue("$edited_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$mentions", JsonSerializer.Serialize(mentions.Select(m => m.ToString())));
        cmd.ExecuteNonQuery();

        var updated = comment with
        {
            Content = newContent,
            EditedAt = DateTimeOffset.UtcNow,
            Mentions = mentions
        };

        CommentEdited?.Invoke(this, new CommentEditedEventArgs { Comment = updated, PreviousContent = comment.Content });

        return updated;
    }

    public void DeleteComment(CommentId commentId)
    {
        var currentUser = _teamManagement.GetCurrentUser();
        var comment = GetComment(commentId) ?? throw new InvalidOperationException("Comment not found");

        if (comment.AuthorId != currentUser.Id)
            throw new UnauthorizedAccessException("Can only delete your own comments");

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Delete child comments first
        Exec(conn, tx, "DELETE FROM comments WHERE parent_id = $id",
            ("$id", commentId.ToString()));

        // Delete the comment
        Exec(conn, tx, "DELETE FROM comments WHERE id = $id",
            ("$id", commentId.ToString()));

        tx.Commit();

        CommentDeleted?.Invoke(this, new CommentDeletedEventArgs { CommentId = commentId, ResourceId = comment.ResourceId });
    }

    public Comment? GetComment(CommentId commentId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM comments WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", commentId.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return MapComment(r);
    }

    public IReadOnlyList<Comment> GetResourceComments(SharedResourceId resourceId, bool includeReplies = true)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();

        if (includeReplies)
        {
            cmd.CommandText = "SELECT * FROM comments WHERE resource_id = $resource_id ORDER BY created_at ASC";
        }
        else
        {
            cmd.CommandText = "SELECT * FROM comments WHERE resource_id = $resource_id AND parent_id IS NULL ORDER BY created_at ASC";
        }

        cmd.Parameters.AddWithValue("$resource_id", resourceId.ToString());

        var comments = new List<Comment>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            comments.Add(MapComment(r));
        }

        return comments;
    }

    public IReadOnlyList<Comment> GetCommentReplies(CommentId parentId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM comments WHERE parent_id = $parent_id ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("$parent_id", parentId.ToString());

        var comments = new List<Comment>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            comments.Add(MapComment(r));
        }

        return comments;
    }

    // ========================================================================
    // Reaction Operations
    // ========================================================================

    public Comment AddReaction(CommentId commentId, string emoji)
    {
        var comment = GetComment(commentId) ?? throw new InvalidOperationException("Comment not found");

        var reactions = new Dictionary<string, int>(comment.Reactions);
        if (reactions.TryGetValue(emoji, out var count))
        {
            reactions[emoji] = count + 1;
        }
        else
        {
            reactions[emoji] = 1;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE comments SET reactions = $reactions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", commentId.ToString());
        cmd.Parameters.AddWithValue("$reactions", JsonSerializer.Serialize(reactions));
        cmd.ExecuteNonQuery();

        var updated = comment with { Reactions = reactions };

        ReactionAdded?.Invoke(this, new ReactionAddedEventArgs { CommentId = commentId, Emoji = emoji });

        return updated;
    }

    public Comment RemoveReaction(CommentId commentId, string emoji)
    {
        var comment = GetComment(commentId) ?? throw new InvalidOperationException("Comment not found");

        var reactions = new Dictionary<string, int>(comment.Reactions);
        if (reactions.TryGetValue(emoji, out var count))
        {
            if (count <= 1)
            {
                reactions.Remove(emoji);
            }
            else
            {
                reactions[emoji] = count - 1;
            }
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE comments SET reactions = $reactions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", commentId.ToString());
        cmd.Parameters.AddWithValue("$reactions", JsonSerializer.Serialize(reactions));
        cmd.ExecuteNonQuery();

        return comment with { Reactions = reactions };
    }

    // ========================================================================
    // Annotation Operations
    // ========================================================================

    public Annotation AddAnnotation(
        SharedResourceId resourceId,
        AnnotationType type,
        string content,
        int startPosition,
        int length,
        string? color = null,
        Dictionary<string, object>? metadata = null)
    {
        var currentUser = _teamManagement.GetCurrentUser();

        var annotation = new Annotation(
            AnnotationId.New(),
            resourceId,
            currentUser.Id,
            type,
            content,
            startPosition,
            length,
            DateTimeOffset.UtcNow,
            null,
            color,
            metadata
        );

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, """
            INSERT INTO annotations (id, resource_id, author_id, annotation_type, content, start_position, length, created_at, updated_at, color, metadata)
            VALUES ($id, $resource_id, $author_id, $type, $content, $start_position, $length, $created_at, $updated_at, $color, $metadata)
            """,
            ("$id", annotation.Id.ToString()),
            ("$resource_id", resourceId.ToString()),
            ("$author_id", currentUser.Id.ToString()),
            ("$type", type.ToString()),
            ("$content", content),
            ("$start_position", startPosition),
            ("$length", length),
            ("$created_at", annotation.CreatedAt.ToString("O")),
            ("$updated_at", DBNull.Value),
            ("$color", color ?? (object)DBNull.Value),
            ("$metadata", metadata != null ? JsonSerializer.Serialize(metadata) : DBNull.Value));

        tx.Commit();

        // Log activity
        _queries.InsertActivity(new ActivityEntry(
            ActivityId.New(),
            currentUser.Id,
            ActivityType.AnnotationAdded,
            $"{type} annotation added",
            null,
            resourceId,
            null,
            null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object> { ["annotationType"] = type.ToString() }
        ));

        AnnotationAdded?.Invoke(this, new AnnotationAddedEventArgs { Annotation = annotation });

        return annotation;
    }

    public Annotation UpdateAnnotation(AnnotationId annotationId, string? content = null, string? color = null, Dictionary<string, object>? metadata = null)
    {
        var currentUser = _teamManagement.GetCurrentUser();
        var annotation = GetAnnotation(annotationId) ?? throw new InvalidOperationException("Annotation not found");

        if (annotation.AuthorId != currentUser.Id)
            throw new UnauthorizedAccessException("Can only edit your own annotations");

        var updated = annotation with
        {
            Content = content ?? annotation.Content,
            Color = color ?? annotation.Color,
            Metadata = metadata ?? annotation.Metadata,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE annotations SET content = $content, color = $color, metadata = $metadata, updated_at = $updated_at
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", annotationId.ToString());
        cmd.Parameters.AddWithValue("$content", updated.Content);
        cmd.Parameters.AddWithValue("$color", updated.Color ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", updated.Metadata != null ? JsonSerializer.Serialize(updated.Metadata) : DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", updated.UpdatedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();

        return updated;
    }

    public void DeleteAnnotation(AnnotationId annotationId)
    {
        var currentUser = _teamManagement.GetCurrentUser();
        var annotation = GetAnnotation(annotationId) ?? throw new InvalidOperationException("Annotation not found");

        if (annotation.AuthorId != currentUser.Id)
            throw new UnauthorizedAccessException("Can only delete your own annotations");

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM annotations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", annotationId.ToString());
        cmd.ExecuteNonQuery();

        AnnotationDeleted?.Invoke(this, new AnnotationDeletedEventArgs { AnnotationId = annotationId, ResourceId = annotation.ResourceId });
    }

    public Annotation? GetAnnotation(AnnotationId annotationId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM annotations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", annotationId.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return MapAnnotation(r);
    }

    public IReadOnlyList<Annotation> GetResourceAnnotations(SharedResourceId resourceId, AnnotationType? type = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();

        var sql = "SELECT * FROM annotations WHERE resource_id = $resource_id";
        if (type.HasValue)
        {
            sql += " AND annotation_type = $type";
        }
        sql += " ORDER BY start_position ASC";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$resource_id", resourceId.ToString());
        if (type.HasValue)
        {
            cmd.Parameters.AddWithValue("$type", type.Value.ToString());
        }

        var annotations = new List<Annotation>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            annotations.Add(MapAnnotation(r));
        }

        return annotations;
    }

    public IReadOnlyList<Annotation> GetUserAnnotations(UserId? userId = null, AnnotationType? type = null)
    {
        var currentUser = _teamManagement.GetCurrentUser();
        var targetUserId = userId ?? currentUser.Id;

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();

        var sql = "SELECT * FROM annotations WHERE author_id = $author_id";
        if (type.HasValue)
        {
            sql += " AND annotation_type = $type";
        }
        sql += " ORDER BY created_at DESC";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$author_id", targetUserId.ToString());
        if (type.HasValue)
        {
            cmd.Parameters.AddWithValue("$type", type.Value.ToString());
        }

        var annotations = new List<Annotation>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            annotations.Add(MapAnnotation(r));
        }

        return annotations;
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private List<UserId> ParseMentions(string content)
    {
        var mentions = new List<UserId>();
        var regex = new Regex(@"@(\w+)");
        var matches = regex.Matches(content);

        foreach (Match match in matches)
        {
            var username = match.Groups[1].Value;
            var user = _teamManagement.GetUserByUsername(username);
            if (user is not null)
            {
                mentions.Add(user.Id);
            }
        }

        return mentions.Distinct().ToList();
    }

    private static Comment MapComment(SqliteDataReader r)
    {
        var mentionsJson = r.GetString(r.GetOrdinal("mentions"));
        var mentions = JsonSerializer.Deserialize<List<string>>(mentionsJson) ?? new();

        var reactionsJson = r.GetString(r.GetOrdinal("reactions"));
        var reactions = JsonSerializer.Deserialize<Dictionary<string, int>>(reactionsJson) ?? new();

        return new Comment(
            new CommentId(Guid.Parse(r.GetString(r.GetOrdinal("id")))),
            new SharedResourceId(Guid.Parse(r.GetString(r.GetOrdinal("resource_id")))),
            new UserId(Guid.Parse(r.GetString(r.GetOrdinal("author_id")))),
            r.GetString(r.GetOrdinal("content")),
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
            r.IsDBNull(r.GetOrdinal("edited_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("edited_at"))),
            r.IsDBNull(r.GetOrdinal("parent_id")) ? null : new CommentId(Guid.Parse(r.GetString(r.GetOrdinal("parent_id")))),
            mentions.Select(m => new UserId(Guid.Parse(m))).ToList(),
            reactions
        );
    }

    private static Annotation MapAnnotation(SqliteDataReader r)
    {
        return new Annotation(
            new AnnotationId(Guid.Parse(r.GetString(r.GetOrdinal("id")))),
            new SharedResourceId(Guid.Parse(r.GetString(r.GetOrdinal("resource_id")))),
            new UserId(Guid.Parse(r.GetString(r.GetOrdinal("author_id")))),
            Enum.Parse<AnnotationType>(r.GetString(r.GetOrdinal("annotation_type"))),
            r.GetString(r.GetOrdinal("content")),
            r.GetInt32(r.GetOrdinal("start_position")),
            r.GetInt32(r.GetOrdinal("length")),
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
            r.IsDBNull(r.GetOrdinal("updated_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at"))),
            r.IsDBNull(r.GetOrdinal("color")) ? null : r.GetString(r.GetOrdinal("color")),
            r.IsDBNull(r.GetOrdinal("metadata")) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(r.GetString(r.GetOrdinal("metadata")))
        );
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
public sealed class CommentAddedEventArgs : EventArgs
{
    public required Comment Comment { get; init; }
}

public sealed class CommentEditedEventArgs : EventArgs
{
    public required Comment Comment { get; init; }
    public required string PreviousContent { get; init; }
}

public sealed class CommentDeletedEventArgs : EventArgs
{
    public required CommentId CommentId { get; init; }
    public required SharedResourceId ResourceId { get; init; }
}

public sealed class ReactionAddedEventArgs : EventArgs
{
    public required CommentId CommentId { get; init; }
    public required string Emoji { get; init; }
}

public sealed class AnnotationAddedEventArgs : EventArgs
{
    public required Annotation Annotation { get; init; }
}

public sealed class AnnotationDeletedEventArgs : EventArgs
{
    public required AnnotationId AnnotationId { get; init; }
    public required SharedResourceId ResourceId { get; init; }
}
