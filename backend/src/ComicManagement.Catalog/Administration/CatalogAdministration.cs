using ComicManagement.Shared.Time;

namespace ComicManagement.Catalog.Administration;

public sealed record CatalogOption(Guid Id, string Name, string? Slug = null);
public sealed record AdminCatalogPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);

public sealed record AdminComicItem(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    string SeriesStatus,
    string PublicationStatus,
    string? CoverAssetId,
    long DefaultPriceCoins,
    bool DefaultVipEligible,
    long Version,
    IReadOnlyList<Guid> GenreIds,
    IReadOnlyList<Guid> AuthorIds,
    IReadOnlyList<Guid> TranslationGroupIds);

public sealed record AdminChapterItem(
    Guid Id,
    Guid ComicId,
    string ChapterLabel,
    int SortOrder,
    long? PriceOverrideCoins,
    bool? VipOverride,
    string PublicationStatus,
    long Version);

public sealed record SaveComicInput(
    Guid? Id,
    string Slug,
    string Title,
    string Description,
    string SeriesStatus,
    string? CoverAssetId,
    long DefaultPriceCoins,
    bool DefaultVipEligible,
    long? ExpectedVersion,
    IReadOnlyList<Guid> GenreIds,
    IReadOnlyList<Guid> AuthorIds,
    IReadOnlyList<Guid> TranslationGroupIds);

public sealed record SaveChapterInput(
    Guid? Id,
    Guid ComicId,
    string ChapterLabel,
    int SortOrder,
    long? PriceOverrideCoins,
    bool? VipOverride,
    long? ExpectedVersion);

public enum CatalogAdministrationOutcome
{
    Succeeded,
    ValidationFailed,
    NotFound,
    Conflict,
    SlugConflict,
    PublicationPreconditionFailed,
}

public sealed record CatalogAdministrationResult<T>(
    CatalogAdministrationOutcome Outcome,
    T? Value = default);

public interface ICatalogAdministrationCriticalSectionObserver
{
    Task AfterResourceLockedAsync(string resourceType, Guid resourceId, CancellationToken token);
    Task AfterComicRootsReadAsync(CancellationToken token) => Task.CompletedTask;
}

public sealed class NoOpCatalogAdministrationCriticalSectionObserver : ICatalogAdministrationCriticalSectionObserver
{
    public Task AfterResourceLockedAsync(string resourceType, Guid resourceId, CancellationToken token) =>
        Task.CompletedTask;
}

public interface ICatalogAdministrationStore
{
    Task<AdminCatalogPage<AdminComicItem>> ListComicsAsync(
        string? search, string? publicationStatus, int page, int pageSize,
        CancellationToken cancellationToken);
    Task<AdminComicItem?> GetComicAsync(Guid id, CancellationToken cancellationToken);
    Task<CatalogAdministrationResult<AdminComicItem>> SaveComicAsync(
        SaveComicInput input, Guid actorId, string correlationId, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<AdminCatalogPage<AdminChapterItem>> ListChaptersAsync(
        Guid? comicId, string? search, string? publicationStatus, int page, int pageSize,
        CancellationToken cancellationToken);
    Task<AdminChapterItem?> GetChapterAsync(Guid id, CancellationToken cancellationToken);
    Task<CatalogAdministrationResult<AdminChapterItem>> SaveChapterAsync(
        SaveChapterInput input, Guid actorId, string correlationId, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogOption>> ListTaxonomyAsync(
        string kind, CancellationToken cancellationToken);
    Task<CatalogAdministrationResult<CatalogOption>> CreateTaxonomyAsync(
        string kind, string name, string? slug, Guid actorId, string correlationId,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<CatalogAdministrationResult<CatalogOption>> UpdateTaxonomyAsync(
        string kind, Guid id, string name, string? slug, Guid actorId, string correlationId,
        CancellationToken cancellationToken);
    Task<CatalogAdministrationResult<CatalogOption>> DeleteTaxonomyAsync(
        string kind, Guid id, Guid actorId, string correlationId, CancellationToken cancellationToken);
}

public sealed class CatalogAdministrationHandler(
    ICatalogAdministrationStore store,
    IClock clock)
{
    public Task<AdminCatalogPage<AdminComicItem>> ListComicsAsync(
        string? search, string? publicationStatus, int page, int pageSize, CancellationToken token) =>
        store.ListComicsAsync(search, publicationStatus, page, pageSize, token);

    public Task<AdminComicItem?> GetComicAsync(Guid id, CancellationToken token) =>
        store.GetComicAsync(id, token);

    public Task<CatalogAdministrationResult<AdminComicItem>> SaveComicAsync(
        SaveComicInput input, Guid actorId, string correlationId, CancellationToken token) =>
        store.SaveComicAsync(input, actorId, correlationId, clock.UtcNow, token);

    public Task<AdminCatalogPage<AdminChapterItem>> ListChaptersAsync(
        Guid? comicId, string? search, string? publicationStatus, int page, int pageSize,
        CancellationToken token) =>
        store.ListChaptersAsync(comicId, search, publicationStatus, page, pageSize, token);

    public Task<AdminChapterItem?> GetChapterAsync(Guid id, CancellationToken token) =>
        store.GetChapterAsync(id, token);

    public Task<CatalogAdministrationResult<AdminChapterItem>> SaveChapterAsync(
        SaveChapterInput input, Guid actorId, string correlationId, CancellationToken token) =>
        store.SaveChapterAsync(input, actorId, correlationId, clock.UtcNow, token);

    public Task<IReadOnlyList<CatalogOption>> ListTaxonomyAsync(
        string kind, CancellationToken token) => store.ListTaxonomyAsync(kind, token);

    public Task<CatalogAdministrationResult<CatalogOption>> CreateTaxonomyAsync(
        string kind, string name, string? slug, Guid actorId, string correlationId,
        CancellationToken token) => store.CreateTaxonomyAsync(
            kind, name, slug, actorId, correlationId, clock.UtcNow, token);
    public Task<CatalogAdministrationResult<CatalogOption>> UpdateTaxonomyAsync(string kind, Guid id,
        string name, string? slug, Guid actorId, string correlationId, CancellationToken token) =>
        store.UpdateTaxonomyAsync(kind, id, name, slug, actorId, correlationId, token);
    public Task<CatalogAdministrationResult<CatalogOption>> DeleteTaxonomyAsync(string kind, Guid id,
        Guid actorId, string correlationId, CancellationToken token) =>
        store.DeleteTaxonomyAsync(kind, id, actorId, correlationId, token);
}
