using ComicManagement.Shared.Time;

namespace ComicManagement.Catalog.Publishing;

public enum CatalogPublicationOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
    ConcurrencyConflict = 3,
    PublicationPreconditionFailed = 4,
}

public sealed record CatalogPublicationState(
    Guid Id,
    string PublicationStatus,
    long Version,
    DateTimeOffset? FirstPublishedAt);

public sealed record CatalogPublicationResult(
    CatalogPublicationOutcome Outcome,
    CatalogPublicationState? State = null);

public interface ICatalogPublicationStore
{
    Task<CatalogPublicationResult> PublishComicAsync(
        Guid comicId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<CatalogPublicationResult> HideComicAsync(
        Guid comicId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<CatalogPublicationResult> PublishChapterAsync(
        Guid chapterId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<CatalogPublicationResult> HideChapterAsync(
        Guid chapterId,
        long expectedVersion,
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
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            comicId,
            expectedVersion,
            store.PublishComicAsync,
            cancellationToken);

    public Task<CatalogPublicationResult> HideComicAsync(
        Guid comicId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            comicId,
            expectedVersion,
            store.HideComicAsync,
            cancellationToken);

    public Task<CatalogPublicationResult> PublishChapterAsync(
        Guid chapterId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            chapterId,
            expectedVersion,
            store.PublishChapterAsync,
            cancellationToken);

    public Task<CatalogPublicationResult> HideChapterAsync(
        Guid chapterId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            chapterId,
            expectedVersion,
            store.HideChapterAsync,
            cancellationToken);

    private Task<CatalogPublicationResult> ExecuteAsync(
        Guid aggregateId,
        long expectedVersion,
        Func<Guid, long, DateTimeOffset, CancellationToken, Task<CatalogPublicationResult>> command,
        CancellationToken cancellationToken)
    {
        if (aggregateId == Guid.Empty || expectedVersion < 1)
        {
            return Task.FromResult(new CatalogPublicationResult(
                CatalogPublicationOutcome.ValidationFailed));
        }

        return command(aggregateId, expectedVersion, clock.UtcNow, cancellationToken);
    }
}
