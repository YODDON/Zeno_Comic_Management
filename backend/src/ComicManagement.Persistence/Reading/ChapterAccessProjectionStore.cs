using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
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
        var rows = await (
                from chapter in dbContext.Chapters.AsNoTracking()
                join comic in dbContext.Comics.AsNoTracking()
                    on chapter.ComicId equals comic.Id
                where ids.Contains(chapter.Id) &&
                      comic.PublicationStatus == ComicPublicationStatuses.Published &&
                      comic.FirstPublishedAt != null &&
                      chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                      dbContext.ChapterPages.Any(page =>
                          page.ChapterId == chapter.Id &&
                          page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                          page.Width > 0 &&
                          page.Height > 0)
                select new
                {
                    chapter.Id,
                    EffectivePriceCoins = chapter.PriceOverrideCoins ?? comic.DefaultPriceCoins,
                    VipEligible = chapter.VipOverride ?? comic.DefaultVipEligible,
                })
            .ToListAsync(cancellationToken);

        // V01 and U02 add the production Membership/ChapterUnlock sources. Keeping
        // both flags false here avoids inventing entitlement storage in R01.
        return rows.ToDictionary(
            row => row.Id,
            row => new ChapterAccessProjection(
                row.Id,
                row.EffectivePriceCoins,
                row.VipEligible,
                HasActiveVip: false,
                HasCoinUnlock: false));
    }

    public async Task<ChapterManifestSource?> GetManifestSourceAsync(
        Guid chapterId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
                from chapter in dbContext.Chapters.AsNoTracking()
                join comic in dbContext.Comics.AsNoTracking()
                    on chapter.ComicId equals comic.Id
                join page in dbContext.ChapterPages.AsNoTracking()
                    on chapter.Id equals page.ChapterId
                where chapter.Id == chapterId &&
                      comic.PublicationStatus == ComicPublicationStatuses.Published &&
                      comic.FirstPublishedAt != null &&
                      chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                      page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                      page.Width > 0 &&
                      page.Height > 0
                orderby page.PageOrder, page.Id
                select new
                {
                    chapter.Id,
                    EffectivePriceCoins = chapter.PriceOverrideCoins ?? comic.DefaultPriceCoins,
                    VipEligible = chapter.VipOverride ?? comic.DefaultVipEligible,
                    page.PageOrder,
                    page.Width,
                    page.Height,
                    page.CloudinaryPublicId,
                })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var first = rows[0];
        return new ChapterManifestSource(
            new ChapterAccessProjection(
                first.Id,
                first.EffectivePriceCoins,
                first.VipEligible,
                HasActiveVip: false,
                HasCoinUnlock: false),
            rows.Select(row => new ChapterManifestPageSource(
                    row.PageOrder,
                    row.Width,
                    row.Height,
                    row.CloudinaryPublicId))
                .ToArray());
    }
}
