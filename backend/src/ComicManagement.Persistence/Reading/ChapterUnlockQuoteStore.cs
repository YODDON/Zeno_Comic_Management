using ComicManagement.Reading.Unlocks;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class ChapterUnlockQuoteStore(AppDbContext dbContext)
    : IChapterUnlockQuoteStore
{
    public async Task<ChapterUnlockQuoteSource?> GetSourceAsync(
        Guid userId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var access = await ChapterAccessQuery.PublicReadableChapters(dbContext, userId)
            .SingleOrDefaultAsync(row => row.ChapterId == chapterId, cancellationToken);
        if (access is null)
        {
            return null;
        }

        var balance = await dbContext.Wallets
            .AsNoTracking()
            .Where(wallet => wallet.UserId == userId)
            .Select(wallet => new
            {
                wallet.PaidAvailable,
                wallet.BonusAvailable,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return new ChapterUnlockQuoteSource(
            ChapterAccessQuery.ToProjection(access),
            balance?.PaidAvailable ?? 0,
            balance?.BonusAvailable ?? 0);
    }

    public async Task AddAsync(
        ChapterUnlockQuote quote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        dbContext.ChapterUnlockQuotes.Add(quote);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
