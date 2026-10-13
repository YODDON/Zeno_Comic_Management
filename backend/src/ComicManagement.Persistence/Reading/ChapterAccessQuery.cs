using System.Security.Cryptography;
using System.Text;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Reading.Access;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class ChapterAccessQueryRow
{
    internal Guid ChapterId { get; init; }

    internal Guid ComicId { get; init; }

    internal long EffectivePriceCoins { get; init; }

    internal bool VipEligible { get; init; }
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

internal static class ChapterAccessQuery
{
    internal static IQueryable<ChapterAccessQueryRow> PublicReadableChapters(
        AppDbContext dbContext) =>
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
        };

    internal static async Task<ChapterAccessManifestRow?> LoadManifestAsync(
        AppDbContext dbContext,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var access = await PublicReadableChapters(dbContext)
            .SingleOrDefaultAsync(row => row.ChapterId == chapterId, cancellationToken);
        if (access is null)
        {
            return null;
        }

        var pages = await dbContext.ChapterPages
            .AsNoTracking()
            .Where(page =>
                page.ChapterId == chapterId &&
                page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                page.Width > 0 &&
                page.Height > 0)
            .OrderBy(page => page.PageOrder)
            .ThenBy(page => page.Id)
            .Select(page => new ReadyChapterPageRow(
                page.Id,
                page.PageOrder,
                page.Width,
                page.Height,
                page.CloudinaryPublicId))
            .ToArrayAsync(cancellationToken);

        return pages.Length == 0
            ? null
            : new ChapterAccessManifestRow(access, pages, CalculateRevision(pages));
    }

    internal static ChapterAccessProjection ToProjection(ChapterAccessQueryRow row) => new(
        row.ChapterId,
        row.EffectivePriceCoins,
        row.VipEligible,
        HasActiveVip: false,
        HasCoinUnlock: false);

    internal static async Task LockAccessRowsAsync(
        AppDbContext dbContext,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var comicId = await dbContext.Chapters
            .AsNoTracking()
            .Where(chapter => chapter.Id == chapterId)
            .Select(chapter => (Guid?)chapter.ComicId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!comicId.HasValue)
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM catalog.comics WHERE id = {comicId.Value} FOR UPDATE",
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM catalog.chapters WHERE id = {chapterId} FOR UPDATE",
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM catalog.chapter_pages WHERE chapter_id = {chapterId} FOR UPDATE",
            cancellationToken);
    }

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
