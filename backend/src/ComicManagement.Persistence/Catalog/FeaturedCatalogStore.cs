using ComicManagement.Administration.Auditing;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Featured;
using ComicManagement.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class FeaturedCatalogStore(
    AppDbContext db,
    IConsistentReadExecutor consistentRead,
    IFeaturedCatalogCriticalSectionObserver observer) : IFeaturedCatalogStore
{
    public Task<FeaturedAdminState> GetAdminAsync(CancellationToken token) =>
        consistentRead.ExecuteAsync<FeaturedAdminState>(async cancellationToken =>
        {
        var version = await db.FeaturedConfigurations.AsNoTracking().Select(x => x.Version).SingleOrDefaultAsync(token);
        var ids = await db.FeaturedComics.AsNoTracking().OrderBy(x => x.DisplayOrder)
            .Select(x => x.ComicId).ToListAsync(token);
        return new(version == 0 ? 1 : version, ids);
        }, token);

    public async Task<IReadOnlyList<FeaturedPublicItem>> GetPublicAsync(CancellationToken token) =>
        await (from comic in PublicComicVisibilityQuery.Apply(db).AsNoTracking()
               join featured in db.FeaturedComics.AsNoTracking() on comic.Id equals featured.ComicId
               orderby featured.DisplayOrder
               select new FeaturedPublicItem(comic.Id, comic.Slug, comic.Title, comic.CoverAssetId))
            .ToListAsync(token);

    public async Task<FeaturedSaveResult> SaveAsync(
        IReadOnlyList<Guid> comicIds, long expectedVersion, Guid actorId,
        string correlationId, DateTimeOffset now, CancellationToken token)
    {
        if (comicIds.Count > 5 || comicIds.Any(x => x == Guid.Empty) || comicIds.Distinct().Count() != comicIds.Count ||
            expectedVersion < 1 || actorId == Guid.Empty || string.IsNullOrWhiteSpace(correlationId))
            return new(FeaturedSaveOutcome.ValidationFailed);

        await using var tx = await db.Database.BeginTransactionAsync(token);
        var configuration = await db.FeaturedConfigurations.FromSqlRaw(
            "SELECT * FROM catalog.featured_configuration WHERE id = 1 FOR UPDATE").SingleOrDefaultAsync(token);
        if (configuration is null) throw new InvalidOperationException("Featured configuration seed is missing.");
        await observer.AfterConfigurationLockedAsync(token);
        if (configuration.Version != expectedVersion) return new(FeaturedSaveOutcome.Conflict);

        // Lock selected Comics in one deterministic order. Publication and page
        // mutations take the same Comic lock before changing public visibility.
        foreach (var comicId in comicIds.Order())
        {
            var locked = await db.Comics.FromSqlInterpolated(
                    $"SELECT * FROM catalog.comics WHERE id = {comicId} FOR UPDATE")
                .SingleOrDefaultAsync(token);
            if (locked is null) return new(FeaturedSaveOutcome.Unpublished);
        }

        now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);

        var validCount = await PublicComicVisibilityQuery.Apply(db)
            .CountAsync(x => comicIds.Contains(x.Id), token);
        if (validCount != comicIds.Count) return new(FeaturedSaveOutcome.Unpublished);

        configuration.Advance(expectedVersion, now);
        db.FeaturedComics.RemoveRange(await db.FeaturedComics.ToListAsync(token));
        await db.SaveChangesAsync(token);
        db.FeaturedComics.AddRange(comicIds.Select((id, index) => FeaturedComic.Create(id, index + 1)));
        db.AuditLogs.Add(AuditLog.Create(Guid.NewGuid(), actorId, "Catalog.FeaturedReordered",
            "FeaturedConfiguration", Guid.Parse("00000000-0000-0000-0000-000000000001"),
            correlationId, now, $"{{\"count\":{comicIds.Count}}}"));
        await db.SaveChangesAsync(token);
        var state = new FeaturedAdminState(configuration.Version, comicIds.ToArray());
        await tx.CommitAsync(token);
        return new(FeaturedSaveOutcome.Succeeded, state);
    }
}
