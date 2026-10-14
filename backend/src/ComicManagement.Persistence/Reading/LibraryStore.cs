using System.Data;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Events;
using ComicManagement.Reading.Library;
using ComicManagement.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class LibraryStore(
    AppDbContext dbContext,
    IOutboxWriter outboxWriter) : ILibraryStore
{
    public async Task<FollowCommandResult> FollowAsync(
        Guid userId,
        Guid comicId,
        DateTimeOffset followedAt,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockFollowAsync(userId, comicId, cancellationToken);

        var isPublic = await IsPublicComic(comicId)
            .AnyAsync(cancellationToken);
        if (!isPublic)
        {
            return new FollowCommandResult(LibraryOutcome.NotFound);
        }

        var existing = await dbContext.ComicFollows
            .AsNoTracking()
            .SingleOrDefaultAsync(
                follow => follow.UserId == userId && follow.ComicId == comicId,
                cancellationToken);
        if (existing is not null)
        {
            return new FollowCommandResult(
                LibraryOutcome.Succeeded,
                new FollowState(existing.ComicId, existing.FollowedAt, Created: false));
        }

        var follow = ComicFollow.Create(userId, comicId, followedAt);
        dbContext.ComicFollows.Add(follow);
        outboxWriter.Enqueue(
            ReadingEventTypes.ComicFollowed,
            new ComicFollowed(userId, comicId, followedAt),
            followedAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new FollowCommandResult(
            LibraryOutcome.Succeeded,
            new FollowState(comicId, followedAt, Created: true));
    }

    public async Task UnfollowAsync(
        Guid userId,
        Guid comicId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockFollowAsync(userId, comicId, cancellationToken);

        var follow = await dbContext.ComicFollows.SingleOrDefaultAsync(
            item => item.UserId == userId && item.ComicId == comicId,
            cancellationToken);
        if (follow is not null)
        {
            dbContext.ComicFollows.Remove(follow);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContinueReadingItem>> ListContinueAsync(
        Guid userId,
        LibraryCursor? cursor,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query =
            from progress in dbContext.ReadingProgress.AsNoTracking()
            join comic in dbContext.Comics.AsNoTracking()
                on progress.ComicId equals comic.Id
            join chapter in dbContext.Chapters.AsNoTracking()
                on progress.LastChapterId equals chapter.Id
            where progress.UserId == userId
            select new
            {
                Progress = progress,
                Comic = comic,
                Chapter = chapter,
                HasReadyPage = dbContext.ChapterPages.Any(page =>
                    page.ChapterId == chapter.Id &&
                    page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                    page.Width > 0 &&
                    page.Height > 0),
                CurrentPageReady = dbContext.ChapterPages.Any(page =>
                    page.ChapterId == chapter.Id &&
                    page.PageOrder == progress.LastPageOrder &&
                    page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                    page.Width > 0 &&
                    page.Height > 0),
                NextReadyPage = dbContext.ChapterPages
                    .Where(page =>
                        page.ChapterId == chapter.Id &&
                        page.PageOrder > progress.LastPageOrder &&
                        page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                        page.Width > 0 &&
                        page.Height > 0)
                    .Select(page => (int?)page.PageOrder)
                    .Min(),
                PreviousReadyPage = dbContext.ChapterPages
                    .Where(page =>
                        page.ChapterId == chapter.Id &&
                        page.PageOrder < progress.LastPageOrder &&
                        page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                        page.Width > 0 &&
                        page.Height > 0)
                    .Select(page => (int?)page.PageOrder)
                    .Max(),
                EffectivePriceCoins = chapter.PriceOverrideCoins ?? comic.DefaultPriceCoins,
            };

        if (cursor is not null)
        {
            query = query.Where(row =>
                row.Progress.LastReadAt < cursor.Timestamp ||
                (row.Progress.LastReadAt == cursor.Timestamp &&
                 row.Progress.ComicId.CompareTo(cursor.ComicId) < 0));
        }

        var rows = await query
            .OrderByDescending(row => row.Progress.LastReadAt)
            .ThenByDescending(row => row.Progress.ComicId)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var isAvailable =
                row.Comic.PublicationStatus == ComicPublicationStatuses.Published &&
                row.Comic.FirstPublishedAt.HasValue &&
                row.Chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                row.HasReadyPage;
            var access = isAvailable
                ? ChapterAccessPolicy.Resolve(
                    new ChapterAccessProjection(
                        row.Chapter.Id,
                        row.EffectivePriceCoins,
                        row.Chapter.VipOverride ?? row.Comic.DefaultVipEligible,
                        HasActiveVip: false,
                        HasCoinUnlock: false),
                    isAuthenticated: true)
                : null;
            var canExposePosition = access?.CanRead == true;
            var remappedPage = row.CurrentPageReady
                ? row.Progress.LastPageOrder
                : row.NextReadyPage ?? row.PreviousReadyPage;
            var remappedOffset = row.CurrentPageReady
                ? row.Progress.LastPageOffsetRatio
                : row.NextReadyPage.HasValue ? 0m : 1m;

            return new ContinueReadingItem(
                row.Progress.ComicId,
                isAvailable ? LibraryAvailability.Available : LibraryAvailability.Unavailable,
                isAvailable ? row.Comic.Slug : null,
                isAvailable ? row.Comic.Title : null,
                isAvailable ? row.Comic.CoverAssetId : null,
                isAvailable ? row.Comic.SeriesStatus : null,
                isAvailable ? row.Chapter.Id : null,
                isAvailable ? row.Chapter.ChapterLabel : null,
                canExposePosition ? remappedPage : null,
                canExposePosition ? remappedOffset : null,
                row.Progress.LastReadAt,
                access?.AccessType.ToString(),
                access?.CanRead == true);
        }).ToArray();
    }

    public async Task<IReadOnlyList<FollowedComicItem>> ListFollowsAsync(
        Guid userId,
        LibraryCursor? cursor,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query =
            from follow in dbContext.ComicFollows.AsNoTracking()
            join comic in dbContext.Comics.AsNoTracking()
                on follow.ComicId equals comic.Id
            where follow.UserId == userId
            select new
            {
                Follow = follow,
                Comic = comic,
                IsPublic = comic.PublicationStatus == ComicPublicationStatuses.Published &&
                    comic.FirstPublishedAt != null &&
                    dbContext.Chapters.Any(chapter =>
                        chapter.ComicId == comic.Id &&
                        chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                        dbContext.ChapterPages.Any(page =>
                            page.ChapterId == chapter.Id &&
                            page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                            page.Width > 0 &&
                            page.Height > 0)),
            };

        if (cursor is not null)
        {
            query = query.Where(row =>
                row.Follow.FollowedAt < cursor.Timestamp ||
                (row.Follow.FollowedAt == cursor.Timestamp &&
                 row.Follow.ComicId.CompareTo(cursor.ComicId) < 0));
        }

        var rows = await query
            .OrderByDescending(row => row.Follow.FollowedAt)
            .ThenByDescending(row => row.Follow.ComicId)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new FollowedComicItem(
            row.Follow.ComicId,
            row.IsPublic ? LibraryAvailability.Available : LibraryAvailability.Unavailable,
            row.IsPublic ? row.Comic.Slug : null,
            row.IsPublic ? row.Comic.Title : null,
            row.IsPublic ? row.Comic.CoverAssetId : null,
            row.IsPublic ? row.Comic.SeriesStatus : null,
            row.Follow.FollowedAt)).ToArray();
    }

    private IQueryable<Comic> IsPublicComic(Guid comicId) => dbContext.Comics
        .AsNoTracking()
        .Where(comic =>
            comic.Id == comicId &&
            comic.PublicationStatus == ComicPublicationStatuses.Published &&
            comic.FirstPublishedAt != null &&
            dbContext.Chapters.Any(chapter =>
                chapter.ComicId == comic.Id &&
                chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                dbContext.ChapterPages.Any(page =>
                    page.ChapterId == chapter.Id &&
                    page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                    page.Width > 0 &&
                    page.Height > 0)));

    private Task LockFollowAsync(
        Guid userId,
        Guid comicId,
        CancellationToken cancellationToken)
    {
        var lockKey = $"follow:{userId:N}:{comicId:N}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }
}
