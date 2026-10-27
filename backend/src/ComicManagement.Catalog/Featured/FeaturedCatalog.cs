namespace ComicManagement.Catalog.Featured;

public sealed class FeaturedComic
{
    private FeaturedComic() { }
    public Guid ComicId { get; private set; }
    public int DisplayOrder { get; private set; }
    public static FeaturedComic Create(Guid comicId, int displayOrder) => new()
    {
        ComicId = comicId,
        DisplayOrder = displayOrder,
    };
}

public sealed class FeaturedConfiguration
{
    private FeaturedConfiguration() { }
    public int Id { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public static FeaturedConfiguration Create(DateTimeOffset now) => new()
    {
        Id = 1,
        Version = 1,
        UpdatedAt = now,
    };
    public void Advance(long expectedVersion, DateTimeOffset now)
    {
        if (Version != expectedVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
        Version++;
        UpdatedAt = now;
    }
}

public sealed record FeaturedAdminState(long Version, IReadOnlyList<Guid> ComicIds);
public sealed record FeaturedPublicItem(Guid ComicId, string Slug, string Title, string? CoverAssetId);

public enum FeaturedSaveOutcome { Succeeded, ValidationFailed, NotFound, Conflict, Unpublished }
public sealed record FeaturedSaveResult(FeaturedSaveOutcome Outcome, FeaturedAdminState? State = null);

public interface IFeaturedCatalogCriticalSectionObserver
{
    Task AfterConfigurationLockedAsync(CancellationToken token);
}

public sealed class NoOpFeaturedCatalogCriticalSectionObserver : IFeaturedCatalogCriticalSectionObserver
{
    public Task AfterConfigurationLockedAsync(CancellationToken token) => Task.CompletedTask;
}

public interface IFeaturedCatalogStore
{
    Task<FeaturedAdminState> GetAdminAsync(CancellationToken token);
    Task<IReadOnlyList<FeaturedPublicItem>> GetPublicAsync(CancellationToken token);
    Task<FeaturedSaveResult> SaveAsync(
        IReadOnlyList<Guid> comicIds, long expectedVersion, Guid actorId,
        string correlationId, DateTimeOffset now, CancellationToken token);
}
