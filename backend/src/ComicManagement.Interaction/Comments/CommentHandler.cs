using ComicManagement.Shared.Time;

namespace ComicManagement.Interaction.Comments;

public static class CommentTargetTypes
{
    public const string Comic = "Comic";
    public const string Chapter = "Chapter";
}

public static class CommentModerationActions
{
    public const string Hide = "Hide";
    public const string Delete = "Delete";
    public const string Restore = "Restore";
}

public enum CommentOutcome
{
    Succeeded,
    ValidationFailed,
    TargetUnavailable,
    NotFound,
    Forbidden,
    InvalidParent,
    ConcurrencyConflict,
    InvalidState,
}

public sealed record CommentItem(
    Guid Id,
    Guid UserId,
    string DisplayName,
    string? AvatarUrl,
    Guid? ComicId,
    Guid? ChapterId,
    Guid? ParentId,
    string Body,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    int ReplyCount,
    string? TargetTitle = null,
    string? TargetSlug = null,
    string? ParentBody = null,
    string? ParentAuthorDisplayName = null);

public sealed record CommentPage(
    IReadOnlyList<CommentItem> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);

public sealed record CommentResult(CommentOutcome Outcome, CommentItem? Comment = null);
public sealed record CommentPageResult(CommentOutcome Outcome, CommentPage? Page = null);
public sealed record CommentModerationAudit(Guid ActorId, string CorrelationId, string Reason);

public interface ICommentStore
{
    Task<CommentPageResult> ListTargetAsync(string targetType, Guid targetId, int page, int pageSize, CancellationToken cancellationToken);
    Task<CommentPageResult> ListRepliesAsync(Guid parentId, int page, int pageSize, CancellationToken cancellationToken);
    Task<CommentPageResult> ListForModerationAsync(string? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<CommentResult> CreateAsync(Comment comment, CancellationToken cancellationToken);
    Task<CommentResult> EditAsync(Guid commentId, Guid userId, string body, long expectedVersion, DateTimeOffset now, CancellationToken cancellationToken);
    Task<CommentResult> DeleteByOwnerAsync(Guid commentId, Guid userId, long expectedVersion, DateTimeOffset now, CancellationToken cancellationToken);
    Task<CommentResult> ModerateAsync(Guid commentId, string action, long expectedVersion, CommentModerationAudit audit, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class CommentHandler(ICommentStore store, IClock clock)
{
    public const int PageSize = 20;
    public const int MaximumPage = 100_000;

    public Task<CommentPageResult> ListTargetAsync(string targetType, Guid targetId, int page, CancellationToken cancellationToken = default) =>
        ValidTarget(targetType, targetId, page)
            ? store.ListTargetAsync(targetType, targetId, page, PageSize, cancellationToken)
            : ValidationPage();

    public Task<CommentPageResult> ListRepliesAsync(Guid parentId, int page, CancellationToken cancellationToken = default) =>
        parentId != Guid.Empty && ValidPage(page)
            ? store.ListRepliesAsync(parentId, page, PageSize, cancellationToken)
            : ValidationPage();

    public Task<CommentPageResult> ListForModerationAsync(string? status, int page, CancellationToken cancellationToken = default) =>
        ValidPage(page) && (status is null || status is CommentStatuses.Visible or CommentStatuses.Hidden or CommentStatuses.Deleted)
            ? store.ListForModerationAsync(status, page, PageSize, cancellationToken)
            : ValidationPage();

    public Task<CommentResult> CreateAsync(Guid userId, Guid? comicId, Guid? chapterId, Guid? parentId, string body, CancellationToken cancellationToken = default)
    {
        try
        {
            return store.CreateAsync(Comment.Create(Guid.NewGuid(), userId, comicId, chapterId, parentId, body, clock.UtcNow), cancellationToken);
        }
        catch (ArgumentException) { return Validation(); }
    }

    public Task<CommentResult> EditAsync(Guid id, Guid userId, string body, long expectedVersion, CancellationToken cancellationToken = default) =>
        ValidMutation(id, userId, expectedVersion) && ValidBody(body)
            ? store.EditAsync(id, userId, body, expectedVersion, clock.UtcNow, cancellationToken)
            : Validation();

    public Task<CommentResult> DeleteAsync(Guid id, Guid userId, long expectedVersion, CancellationToken cancellationToken = default) =>
        ValidMutation(id, userId, expectedVersion)
            ? store.DeleteByOwnerAsync(id, userId, expectedVersion, clock.UtcNow, cancellationToken)
            : Validation();

    public Task<CommentResult> ModerateAsync(Guid id, string action, long expectedVersion, Guid actorId, string correlationId, string reason, CancellationToken cancellationToken = default) =>
        ValidMutation(id, actorId, expectedVersion) &&
        action is CommentModerationActions.Hide or CommentModerationActions.Delete or CommentModerationActions.Restore &&
        !string.IsNullOrWhiteSpace(correlationId) && ValidReason(reason)
            ? store.ModerateAsync(id, action, expectedVersion, new(actorId, correlationId, reason.Trim()), clock.UtcNow, cancellationToken)
            : Validation();

    private static bool ValidTarget(string type, Guid id, int page) =>
        type is CommentTargetTypes.Comic or CommentTargetTypes.Chapter && id != Guid.Empty && ValidPage(page);
    private static bool ValidPage(int page) => page is >= 1 and <= MaximumPage;
    private static bool ValidMutation(Guid id, Guid userId, long version) => id != Guid.Empty && userId != Guid.Empty && version >= 1;
    private static bool ValidBody(string? body) => !string.IsNullOrWhiteSpace(body) && body.Trim().Length <= Comment.MaximumBodyLength;
    private static bool ValidReason(string? reason) => !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= 500;
    private static Task<CommentResult> Validation() => Task.FromResult(new CommentResult(CommentOutcome.ValidationFailed));
    private static Task<CommentPageResult> ValidationPage() => Task.FromResult(new CommentPageResult(CommentOutcome.ValidationFailed));
}
