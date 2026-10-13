using ComicManagement.Reading.Access;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class ChapterAccessProjectionStore(AppDbContext dbContext)
    : IChapterAccessProjectionStore
{
    public async Task<IReadOnlyDictionary<Guid, ChapterAccessProjection>> GetManyAsync(
        IReadOnlyCollection<Guid> chapterIds,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chapterIds);
        if (chapterIds.Count == 0)
        {
            return new Dictionary<Guid, ChapterAccessProjection>();
        }

        var ids = chapterIds.Distinct().ToArray();
        var rows = await ChapterAccessQuery.PublicReadableChapters(dbContext)
            .Where(row => ids.Contains(row.ChapterId))
            .ToListAsync(cancellationToken);

        // V01 and U02 add the production Membership/ChapterUnlock sources. Keeping
        // both flags false here avoids inventing entitlement storage in R01.
        return rows.ToDictionary(
            row => row.ChapterId,
            ChapterAccessQuery.ToProjection);
    }

    public async Task<ChapterManifestSource?> GetManifestSourceAsync(
        Guid chapterId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ChapterAccessQuery.LoadManifestAsync(
            dbContext,
            chapterId,
            cancellationToken);
        if (manifest is null)
        {
            return null;
        }

        return new ChapterManifestSource(
            ChapterAccessQuery.ToProjection(manifest.Access),
            manifest.Pages.Select(row => new ChapterManifestPageSource(
                    row.PageOrder,
                    row.Width,
                    row.Height,
                    row.AssetKey))
                .ToArray());
    }

    public async Task<ChapterPageContentSource?> GetPageContentSourceAsync(
        Guid chapterId,
        int pageOrder,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ChapterAccessQuery.LoadManifestAsync(
            dbContext,
            chapterId,
            cancellationToken);
        var page = manifest?.Pages.SingleOrDefault(row => row.PageOrder == pageOrder);
        if (manifest is null || page is null)
        {
            return null;
        }

        return new ChapterPageContentSource(
            ChapterAccessQuery.ToProjection(manifest.Access),
            new ChapterManifestPageSource(
                page.PageOrder,
                page.Width,
                page.Height,
                page.AssetKey));
    }
}
