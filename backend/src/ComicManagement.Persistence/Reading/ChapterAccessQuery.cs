using System.Security.Cryptography;
using System.Text;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Membership.Plans;
using ComicManagement.Reading.Access;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class ChapterAccessQueryRow
{
    internal Guid ChapterId { get; init; }

    internal Guid ComicId { get; init; }

    internal long EffectivePriceCoins { get; init; }

    internal bool VipEligible { get; init; }

    internal bool HasActiveVip { get; init; }

    internal bool HasCoinUnlock { get; init; }
}

internal sealed record ReadyChapterPageRow(
    Guid PageId,
    int PageOrder,
    int Width,
    int Height,
    string AssetKey);

internal sealed record ChapterAccessManifestRow(
    ChapterAccessQueryRow Access,
    IReadOnlyList<ReadyChapterPageRow> Pages,
    string Revision);

internal sealed record ChapterAccessManifestPageRow(
    Guid ChapterId,
    Guid ComicId,
    long EffectivePriceCoins,
    bool VipEligible,
    bool HasActiveVip,
    bool HasCoinUnlock,
    Guid PageId,
    int PageOrder,
    int Width,
    int Height,
    string AssetKey);

internal static class ChapterAccessQuery
{
    internal static IQueryable<ChapterAccessQueryRow> PublicReadableChapters(
        AppDbContext dbContext,
        Guid? userId = null) =>
        from chapter in dbContext.Chapters.AsNoTracking()
        join comic in dbContext.Comics.AsNoTracking()
            on chapter.ComicId equals comic.Id
        where comic.PublicationStatus == ComicPublicationStatuses.Published &&
              comic.FirstPublishedAt != null &&
              chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
              dbContext.ChapterPages.Any(page =>
                  page.ChapterId == chapter.Id &&
                  page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                  page.Width > 0 &&
                  page.Height > 0)
        select new ChapterAccessQueryRow
        {
            ChapterId = chapter.Id,
            ComicId = chapter.ComicId,
            EffectivePriceCoins = chapter.PriceOverrideCoins ?? comic.DefaultPriceCoins,
            VipEligible = chapter.VipOverride ?? comic.DefaultVipEligible,
            HasActiveVip = userId.HasValue && dbContext.VipSubscriptions.Any(subscription =>
                subscription.UserId == userId.Value &&
                subscription.ExpiresAt > DateTimeOffset.UtcNow),
            HasCoinUnlock = userId.HasValue && dbContext.ChapterUnlocks.Any(unlock =>
                unlock.UserId == userId.Value &&
                unlock.ChapterId == chapter.Id),
        };

    internal static async Task<ChapterAccessManifestRow?> LoadManifestAsync(
        AppDbContext dbContext,
        Guid chapterId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        // Publication/access state and the Ready manifest must come from the same
        // PostgreSQL statement. Splitting these reads allows a concurrent hide,
        // reprice or page transition to produce a manifest for stale access state.
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
            select new ChapterAccessManifestPageRow(
                chapter.Id,
                chapter.ComicId,
                chapter.PriceOverrideCoins ?? comic.DefaultPriceCoins,
                chapter.VipOverride ?? comic.DefaultVipEligible,
                userId.HasValue && dbContext.VipSubscriptions.Any(subscription =>
                    subscription.UserId == userId.Value &&
                    subscription.ExpiresAt > DateTimeOffset.UtcNow),
                userId.HasValue && dbContext.ChapterUnlocks.Any(unlock =>
                    unlock.UserId == userId.Value &&
                    unlock.ChapterId == chapter.Id),
                page.Id,
                page.PageOrder,
                page.Width,
                page.Height,
                page.CloudinaryPublicId))
            .ToArrayAsync(cancellationToken);

        if (rows.Length == 0)
        {
            return null;
        }

        var first = rows[0];
        var access = new ChapterAccessQueryRow
        {
            ChapterId = first.ChapterId,
            ComicId = first.ComicId,
            EffectivePriceCoins = first.EffectivePriceCoins,
            VipEligible = first.VipEligible,
            HasActiveVip = first.HasActiveVip,
            HasCoinUnlock = first.HasCoinUnlock,
        };
        var pages = rows
            .Select(row => new ReadyChapterPageRow(
                row.PageId,
                row.PageOrder,
                row.Width,
                row.Height,
                row.AssetKey))
            .ToArray();

        return new ChapterAccessManifestRow(access, pages, CalculateRevision(pages));
    }

    internal static ChapterAccessProjection ToProjection(ChapterAccessQueryRow row) => new(
        row.ChapterId,
        row.EffectivePriceCoins,
        row.VipEligible,
        row.HasActiveVip,
        HasCoinUnlock: row.HasCoinUnlock,
        ActiveVipDiscountPercent: row.HasActiveVip
            ? VipPlan.FixedDiscountPercent
            : 0);

    private static string CalculateRevision(IReadOnlyList<ReadyChapterPageRow> pages)
    {
        var canonical = new StringBuilder(pages.Count * 96);
        foreach (var page in pages)
        {
            canonical
                .Append(page.PageId.ToString("N"))
                .Append('|')
                .Append(page.PageOrder)
                .Append('|')
                .Append(page.Width)
                .Append('|')
                .Append(page.Height)
                .Append('|')
                .Append(page.AssetKey)
                .Append(';');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
