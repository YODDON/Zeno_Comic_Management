using ComicManagement.Interaction.Ratings;
using ComicManagement.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class ComicRatingStore(
    AppDbContext dbContext,
    IConsistentReadExecutor consistentReadExecutor,
    IComicRatingReadObserver readObserver) : IComicRatingStore
{
    public async Task<ComicRatingResult> GetAsync(
        Guid comicId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    => await consistentReadExecutor.ExecuteAsync(async transactionCancellationToken =>
    {
        if (!await IsPublicAsync(comicId, transactionCancellationToken))
        {
            return new ComicRatingResult(ComicRatingOutcome.ComicUnavailable);
        }

        await readObserver.AfterPublicVisibilityReadAsync(transactionCancellationToken);

        return new ComicRatingResult(
            ComicRatingOutcome.Succeeded,
            await LoadSummaryAsync(comicId, userId, transactionCancellationToken));
    }, cancellationToken);

    public async Task<ComicRatingResult> UpsertAsync(
        Guid comicId,
        Guid userId,
        short stars,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAndCheckPublicAsync(comicId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ComicRatingResult(ComicRatingOutcome.ComicUnavailable);
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO interaction.comic_ratings
                (user_id, comic_id, stars, created_at, updated_at)
            VALUES
                ({userId}, {comicId}, {stars}, clock_timestamp(), clock_timestamp())
            ON CONFLICT (user_id, comic_id) DO UPDATE
            SET stars = EXCLUDED.stars,
                updated_at = clock_timestamp()
            """, cancellationToken);

        var summary = await LoadSummaryAsync(comicId, userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ComicRatingResult(ComicRatingOutcome.Succeeded, summary);
    }

    public async Task<ComicRatingResult> DeleteAsync(
        Guid comicId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAndCheckPublicAsync(comicId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ComicRatingResult(ComicRatingOutcome.ComicUnavailable);
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM interaction.comic_ratings
            WHERE user_id = {userId} AND comic_id = {comicId}
            """, cancellationToken);

        var summary = await LoadSummaryAsync(comicId, userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ComicRatingResult(ComicRatingOutcome.Succeeded, summary);
    }

    private Task<bool> IsPublicAsync(Guid comicId, CancellationToken cancellationToken) =>
        PublicComicVisibilityQuery.Apply(dbContext)
            .AsNoTracking()
            .AnyAsync(comic => comic.Id == comicId, cancellationToken);

    private async Task<bool> LockAndCheckPublicAsync(
        Guid comicId,
        CancellationToken cancellationToken)
    {
        var lockedRows = await dbContext.Comics
            .FromSqlInterpolated($"""
                SELECT *
                FROM catalog.comics
                WHERE id = {comicId}
                FOR SHARE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return lockedRows.Count == 1 && await IsPublicAsync(comicId, cancellationToken);
    }

    private async Task<ComicRatingSummary> LoadSummaryAsync(
        Guid comicId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var summary = await dbContext.Comics
            .AsNoTracking()
            .Where(comic => comic.Id == comicId)
            .Select(_ => new
            {
                AverageStars = dbContext.ComicRatings
                    .Where(rating => rating.ComicId == comicId)
                    .Average(rating => (decimal?)rating.Stars) ?? 0m,
                RatingCount = dbContext.ComicRatings.Count(rating => rating.ComicId == comicId),
                MyRating = userId == null
                    ? null
                    : dbContext.ComicRatings
                        .Where(rating =>
                            rating.ComicId == comicId &&
                            rating.UserId == userId.Value)
                        .Select(rating => (short?)rating.Stars)
                        .SingleOrDefault(),
            })
            .SingleAsync(cancellationToken);

        return new ComicRatingSummary(
            summary.AverageStars,
            summary.RatingCount,
            summary.MyRating);
    }
}
