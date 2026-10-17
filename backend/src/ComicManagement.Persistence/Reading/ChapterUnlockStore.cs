using System.Data;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Membership.Plans;
using ComicManagement.Reading.Events;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Shared.Messaging;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Spending;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ComicManagement.Persistence;

internal interface IChapterUnlockCriticalSectionObserver
{
    Task AfterWalletLockedAsync(CancellationToken cancellationToken);
}

internal sealed class NoOpChapterUnlockCriticalSectionObserver :
    IChapterUnlockCriticalSectionObserver
{
    public Task AfterWalletLockedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class ChapterUnlockStore(
    AppDbContext dbContext,
    WalletSpendEngine spendEngine,
    IOutboxWriter outboxWriter,
    IChapterUnlockCriticalSectionObserver observer) : IChapterUnlockStore
{
    public async Task<UnlockChapterResult> UnlockAsync(
        Guid userId,
        Guid chapterId,
        Guid quoteId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var quotedAmount = await dbContext.ChapterUnlockQuotes
            .AsNoTracking()
            .Where(quote => quote.Id == quoteId && quote.UserId == userId)
            .Select(quote => (long?)quote.FinalPriceCoins)
            .SingleOrDefaultAsync(cancellationToken);
        if (quotedAmount is null)
        {
            return Failed(UnlockChapterOutcome.NotFound);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        try
        {
            var initialServerTime = await GetServerTimeAsync(cancellationToken);
            var walletLock = await spendEngine.AcquireAsync(
                userId,
                initialServerTime,
                cancellationToken);
            if (walletLock.Outcome != SpendCoinsOutcome.Succeeded || walletLock.Wallet is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed(MapSpendOutcome(walletLock.Outcome));
            }

            await observer.AfterWalletLockedAsync(cancellationToken);
            await AcquireVipSubscriptionLockAsync(userId, cancellationToken);
            var serverNow = await GetServerTimeAsync(cancellationToken);

            var replay = await TryLoadIdempotentReplayAsync(
                userId,
                chapterId,
                quoteId,
                idempotencyKey,
                cancellationToken);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            var quote = await dbContext.ChapterUnlockQuotes
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM reading.chapter_unlock_quotes
                    WHERE id = {quoteId}
                    FOR SHARE
                    """)
                .SingleOrDefaultAsync(cancellationToken);
            if (quote is null || quote.UserId != userId || quote.ChapterId != chapterId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed(UnlockChapterOutcome.NotFound);
            }

            var chapter = await dbContext.Chapters
                .FromSqlInterpolated($"""
                    SELECT * FROM catalog.chapters
                    WHERE id = {chapterId}
                    FOR SHARE
                    """)
                .SingleOrDefaultAsync(cancellationToken);
            if (chapter is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed(UnlockChapterOutcome.NotFound);
            }

            var comic = await dbContext.Comics
                .FromSqlInterpolated($"""
                    SELECT * FROM catalog.comics
                    WHERE id = {chapter.ComicId}
                    FOR SHARE
                    """)
                .SingleOrDefaultAsync(cancellationToken);
            var pages = await dbContext.ChapterPages
                .FromSqlInterpolated($"""
                    SELECT * FROM catalog.chapter_pages
                    WHERE chapter_id = {chapterId}
                    ORDER BY page_order, id
                    FOR SHARE
                    """)
                .ToListAsync(cancellationToken);
            if (comic is null ||
                comic.PublicationStatus != ComicPublicationStatuses.Published ||
                comic.FirstPublishedAt is null ||
                chapter.PublicationStatus != ChapterPublicationStatuses.Published ||
                !pages.Any(page =>
                    page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                    page.Width > 0 &&
                    page.Height > 0))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed(UnlockChapterOutcome.NotFound);
            }

            var existingUnlock = await dbContext.ChapterUnlocks
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    unlock => unlock.UserId == userId && unlock.ChapterId == chapterId,
                    cancellationToken);
            if (existingUnlock is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return Succeeded(existingUnlock, alreadyUnlocked: true);
            }

            if (serverNow >= quote.ExpiresAt)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed(UnlockChapterOutcome.QuoteExpired);
            }

            var subscription = await dbContext.VipSubscriptions
                .AsNoTracking()
                .SingleOrDefaultAsync(value => value.UserId == userId, cancellationToken);
            var hasActiveVip = subscription?.ExpiresAt > serverNow;
            var basePrice = chapter.PriceOverrideCoins ?? comic.DefaultPriceCoins;
            var vipEligible = chapter.VipOverride ?? comic.DefaultVipEligible;
            if (basePrice == 0 || hasActiveVip && vipEligible)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed(UnlockChapterOutcome.UnlockNotRequired);
            }

            var discountPercent = hasActiveVip
                ? VipPlan.FixedDiscountPercent
                : 0;
            var currentFinalPrice = ChapterUnlockPricingPolicy.CalculateFinalPrice(
                basePrice,
                discountPercent);
            var currentRevision = ChapterUnlockPricingPolicy.CalculateRevision(
                chapterId,
                basePrice,
                discountPercent);
            if (quote.BasePriceCoins != basePrice ||
                quote.VipDiscountPercent != discountPercent ||
                quote.FinalPriceCoins != currentFinalPrice ||
                quote.PriceRevision != currentRevision ||
                quotedAmount.Value != currentFinalPrice)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed(UnlockChapterOutcome.PriceChanged);
            }

            var unlockId = Guid.NewGuid();
            var spend = await spendEngine.ApplyLockedAsync(
                new SpendCoinsCommand(
                    userId,
                    currentFinalPrice,
                    WalletOperationTypes.ChapterUnlock,
                    WalletOperationSourceTypes.ReadingUnlock,
                    unlockId,
                    idempotencyKey),
                walletLock,
                serverNow,
                cancellationToken);
            if (spend.Outcome != SpendCoinsOutcome.Succeeded || spend.Receipt is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed(MapSpendOutcome(spend.Outcome));
            }

            var unlock = ChapterUnlock.Create(
                unlockId,
                userId,
                chapterId,
                quoteId,
                spend.Receipt.OperationId,
                currentFinalPrice,
                spend.Receipt.BonusCoins,
                spend.Receipt.PaidCoins,
                walletLock.Wallet.PaidAvailable,
                walletLock.Wallet.BonusAvailable,
                serverNow);
            dbContext.ChapterUnlocks.Add(unlock);
            outboxWriter.Enqueue(
                ReadingEventTypes.ChapterUnlocked,
                new ChapterUnlocked(
                    unlock.Id,
                    unlock.UserId,
                    unlock.ChapterId,
                    unlock.QuoteId,
                    unlock.WalletOperationId,
                    unlock.PricePaidCoins,
                    unlock.BonusSpentCoins,
                    unlock.PaidSpentCoins,
                    unlock.UnlockedAt),
                serverNow);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Succeeded(unlock, alreadyUnlocked: false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<UnlockChapterResult?> TryLoadIdempotentReplayAsync(
        Guid userId,
        Guid chapterId,
        Guid quoteId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var operation = await dbContext.WalletOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.UserId == userId &&
                candidate.OperationType == WalletOperationTypes.ChapterUnlock &&
                candidate.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (operation is null)
        {
            return null;
        }

        var unlock = await dbContext.ChapterUnlocks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.WalletOperationId == operation.Id,
                cancellationToken);
        if (unlock is null ||
            operation.SourceType != WalletOperationSourceTypes.ReadingUnlock ||
            operation.SourceId != unlock.Id ||
            unlock.UserId != userId ||
            unlock.ChapterId != chapterId ||
            unlock.QuoteId != quoteId)
        {
            return Failed(UnlockChapterOutcome.IdempotencyConflict);
        }

        return Succeeded(unlock, alreadyUnlocked: false);
    }

    private async Task AcquireVipSubscriptionLockAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("VIP lock requires a transaction.");
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = $"vip-subscription:{userId:N}";
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DateTimeOffset> GetServerTimeAsync(
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Server time requires a transaction.");
        command.CommandText = "SELECT clock_timestamp();";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("PostgreSQL did not return server time."),
        };
    }

    private static UnlockChapterResult Succeeded(
        ChapterUnlock unlock,
        bool alreadyUnlocked) => new(
        UnlockChapterOutcome.Succeeded,
        new UnlockChapterState(
            unlock.ChapterId,
            unlock.BonusSpentCoins,
            unlock.PaidSpentCoins,
            unlock.WalletPaidAvailableAfter,
            unlock.WalletBonusAvailableAfter,
            alreadyUnlocked));

    private static UnlockChapterResult Failed(UnlockChapterOutcome outcome) => new(outcome);

    private static UnlockChapterOutcome MapSpendOutcome(SpendCoinsOutcome outcome) => outcome switch
    {
        SpendCoinsOutcome.ValidationFailed => UnlockChapterOutcome.ValidationFailed,
        SpendCoinsOutcome.InsufficientCoins => UnlockChapterOutcome.InsufficientCoins,
        SpendCoinsOutcome.IdempotencyConflict => UnlockChapterOutcome.IdempotencyConflict,
        SpendCoinsOutcome.WalletUnavailable => UnlockChapterOutcome.NotFound,
        SpendCoinsOutcome.InvariantViolation => UnlockChapterOutcome.InvariantViolation,
        _ => UnlockChapterOutcome.InvariantViolation,
    };
}
