namespace ComicManagement.Interaction.Comments;

public static class CommentStatuses
{
    public const string Visible = "Visible";
    public const string Hidden = "Hidden";
    public const string Deleted = "Deleted";
}

public sealed class Comment
{
    public const int MaximumBodyLength = 2_000;

    private Comment() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? ComicId { get; private set; }
    public Guid? ChapterId { get; private set; }
    public Guid? ParentId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public string Status { get; private set; } = CommentStatuses.Visible;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }

    public static Comment Create(
        Guid id,
        Guid userId,
        Guid? comicId,
        Guid? chapterId,
        Guid? parentId,
        string body,
        DateTimeOffset createdAt)
    {
        ValidateIdentity(id, userId, comicId, chapterId);
        var normalizedBody = NormalizeBody(body);
        EnsureUtc(createdAt, nameof(createdAt));
        return new Comment
        {
            Id = id,
            UserId = userId,
            ComicId = comicId,
            ChapterId = chapterId,
            ParentId = parentId,
            Body = normalizedBody,
            Status = CommentStatuses.Visible,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Version = 1,
        };
    }

    public void Edit(string body, long expectedVersion, DateTimeOffset updatedAt)
    {
        RequireVersion(expectedVersion);
        if (Status != CommentStatuses.Visible)
            throw new CommentRuleViolationException(CommentRuleViolation.InvalidState);
        Body = NormalizeBody(body);
        Advance(updatedAt);
    }

    public void Delete(long expectedVersion, DateTimeOffset updatedAt)
    {
        RequireVersion(expectedVersion);
        if (Status == CommentStatuses.Deleted)
            throw new CommentRuleViolationException(CommentRuleViolation.InvalidState);
        Status = CommentStatuses.Deleted;
        Advance(updatedAt);
    }

    public void Hide(long expectedVersion, DateTimeOffset updatedAt)
    {
        RequireVersion(expectedVersion);
        if (Status != CommentStatuses.Visible)
            throw new CommentRuleViolationException(CommentRuleViolation.InvalidState);
        Status = CommentStatuses.Hidden;
        Advance(updatedAt);
    }

    public void Restore(long expectedVersion, DateTimeOffset updatedAt)
    {
        RequireVersion(expectedVersion);
        if (Status != CommentStatuses.Hidden)
            throw new CommentRuleViolationException(CommentRuleViolation.InvalidState);
        Status = CommentStatuses.Visible;
        Advance(updatedAt);
    }

    private static void ValidateIdentity(Guid id, Guid userId, Guid? comicId, Guid? chapterId)
    {
        if (id == Guid.Empty || userId == Guid.Empty || (comicId.HasValue == chapterId.HasValue))
            throw new ArgumentException("A comment requires IDs and exactly one target.");
        if (comicId == Guid.Empty || chapterId == Guid.Empty)
            throw new ArgumentException("Target ID cannot be empty.");
    }

    private static string NormalizeBody(string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var normalized = body.Trim();
        if (normalized.Length > MaximumBodyLength)
            throw new ArgumentOutOfRangeException(nameof(body));
        return normalized;
    }

    private void RequireVersion(long expectedVersion)
    {
        if (expectedVersion != Version)
            throw new CommentRuleViolationException(CommentRuleViolation.ConcurrencyConflict);
    }

    private void Advance(DateTimeOffset updatedAt)
    {
        EnsureUtc(updatedAt, nameof(updatedAt));
        if (updatedAt < UpdatedAt)
            throw new ArgumentOutOfRangeException(nameof(updatedAt));
        UpdatedAt = updatedAt;
        Version = checked(Version + 1);
    }

    private static void EnsureUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.", name);
    }
}

public enum CommentRuleViolation { ConcurrencyConflict, InvalidState }

public sealed class CommentRuleViolationException(CommentRuleViolation violation) : Exception
{
    public CommentRuleViolation Violation { get; } = violation;
}
