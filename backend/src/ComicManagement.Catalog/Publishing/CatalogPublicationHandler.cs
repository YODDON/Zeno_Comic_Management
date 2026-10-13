using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Shared.Time;

namespace ComicManagement.Catalog.Publishing;

public enum CatalogPublicationOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
    ConcurrencyConflict = 3,
    PublicationPreconditionFailed = 4,
    SlugConflict = 5,
}

public sealed record CatalogPublicationState(
    Guid Id,
    string PublicationStatus,
    long Version,
    DateTimeOffset? FirstPublishedAt,
    string? Slug = null);

public sealed record CatalogPublicationResult(
    CatalogPublicationOutcome Outcome,
    CatalogPublicationState? State = null);

public sealed record CatalogCommandAudit(
    Guid ActorId,
    string Action,
    string TargetType,
    string CorrelationId);

public sealed record ComicPublicationFacts(bool HasValidPublishedChapter);

public sealed record ChapterPublicationFacts(
    string ParentComicPublicationStatus,
    bool HasValidPage,
    bool OtherValidPublishedChapterExists);

public interface ICatalogPublicationStore
{
    Task<CatalogPublicationResult> ExecuteComicAsync(
        Guid comicId,
        CatalogCommandAudit audit,
        Action<Comic, ComicPublicationFacts> transition,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<CatalogPublicationResult> ExecuteChapterAsync(
        Guid chapterId,
        CatalogCommandAudit audit,
        Action<Chapter, ChapterPublicationFacts> transition,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}

public sealed class CatalogPublicationHandler(
    ICatalogPublicationStore store,
    IClock clock)
{
    public Task<CatalogPublicationResult> PublishComicAsync(
        Guid comicId,
        long expectedVersion,
        Guid actorId,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        ExecuteComicAsync(
            comicId,
            expectedVersion,
            actorId,
            correlationId,
            "Catalog.ComicPublished",
            static (comic, facts, version, occurredAt) =>
                comic.Publish(facts.HasValidPublishedChapter, version, occurredAt),
            cancellationToken);

    public Task<CatalogPublicationResult> HideComicAsync(
        Guid comicId,
        long expectedVersion,
        Guid actorId,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        ExecuteComicAsync(
            comicId,
            expectedVersion,
            actorId,
            correlationId,
            "Catalog.ComicHidden",
            static (comic, _, version, occurredAt) => comic.Hide(version, occurredAt),
            cancellationToken);

    public Task<CatalogPublicationResult> ChangeComicSlugAsync(
        Guid comicId,
        string slug,
        long expectedVersion,
        Guid actorId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (!Comic.IsValidSlug(slug))
        {
            return ValidationFailed();
        }

        return ExecuteComicAsync(
            comicId,
            expectedVersion,
            actorId,
            correlationId,
            "Catalog.ComicSlugChanged",
            (comic, _, version, occurredAt) => comic.ChangeSlug(slug, version, occurredAt),
            cancellationToken);
    }

    public Task<CatalogPublicationResult> PublishChapterAsync(
        Guid chapterId,
        long expectedVersion,
        Guid actorId,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        ExecuteChapterAsync(
            chapterId,
            expectedVersion,
            actorId,
            correlationId,
            "Catalog.ChapterPublished",
            static (chapter, facts, version, occurredAt) =>
                chapter.Publish(facts.HasValidPage, version, occurredAt),
            cancellationToken);

    public Task<CatalogPublicationResult> HideChapterAsync(
        Guid chapterId,
        long expectedVersion,
        Guid actorId,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        ExecuteChapterAsync(
            chapterId,
            expectedVersion,
            actorId,
            correlationId,
            "Catalog.ChapterHidden",
            static (chapter, facts, version, occurredAt) =>
            {
                var canHide =
                    facts.ParentComicPublicationStatus != ComicPublicationStatuses.Published ||
                    chapter.PublicationStatus != ChapterPublicationStatuses.Published ||
                    facts.OtherValidPublishedChapterExists;
                chapter.Hide(canHide, version, occurredAt);
            },
            cancellationToken);

    private Task<CatalogPublicationResult> ExecuteComicAsync(
        Guid aggregateId,
        long expectedVersion,
        Guid actorId,
        string correlationId,
        string action,
        Action<Comic, ComicPublicationFacts, long, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        if (!IsValidCommand(aggregateId, expectedVersion, actorId, correlationId))
        {
            return ValidationFailed();
        }

        var occurredAt = clock.UtcNow;
        return store.ExecuteComicAsync(
            aggregateId,
            new CatalogCommandAudit(actorId, action, "Comic", correlationId),
            (comic, facts) => transition(comic, facts, expectedVersion, occurredAt),
            occurredAt,
            cancellationToken);
    }

    private Task<CatalogPublicationResult> ExecuteChapterAsync(
        Guid aggregateId,
        long expectedVersion,
        Guid actorId,
        string correlationId,
        string action,
        Action<Chapter, ChapterPublicationFacts, long, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        if (!IsValidCommand(aggregateId, expectedVersion, actorId, correlationId))
        {
            return ValidationFailed();
        }

        var occurredAt = clock.UtcNow;
        return store.ExecuteChapterAsync(
            aggregateId,
            new CatalogCommandAudit(actorId, action, "Chapter", correlationId),
            (chapter, facts) => transition(chapter, facts, expectedVersion, occurredAt),
            occurredAt,
            cancellationToken);
    }

    private static bool IsValidCommand(
        Guid aggregateId,
        long expectedVersion,
        Guid actorId,
        string correlationId) =>
        aggregateId != Guid.Empty &&
        expectedVersion >= 1 &&
        actorId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(correlationId);

    private static Task<CatalogPublicationResult> ValidationFailed() =>
        Task.FromResult(new CatalogPublicationResult(
            CatalogPublicationOutcome.ValidationFailed));
}
