namespace ComicManagement.Reading.Unlocks;

public sealed class ChapterUnlock
{
    private ChapterUnlock()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid ChapterId { get; private set; }

    public Guid QuoteId { get; private set; }

    public Guid WalletOperationId { get; private set; }

    public long PricePaidCoins { get; private set; }

    public long BonusSpentCoins { get; private set; }

    public long PaidSpentCoins { get; private set; }

    public long WalletPaidAvailableAfter { get; private set; }

    public long WalletBonusAvailableAfter { get; private set; }

    public DateTimeOffset UnlockedAt { get; private set; }

    public static ChapterUnlock Create(
        Guid id,
        Guid userId,
        Guid chapterId,
        Guid quoteId,
        Guid walletOperationId,
        long pricePaidCoins,
        long bonusSpentCoins,
        long paidSpentCoins,
        long walletPaidAvailableAfter,
        long walletBonusAvailableAfter,
        DateTimeOffset unlockedAt)
    {
        EnsureIdentifier(id, nameof(id));
        EnsureIdentifier(userId, nameof(userId));
        EnsureIdentifier(chapterId, nameof(chapterId));
        EnsureIdentifier(quoteId, nameof(quoteId));
        EnsureIdentifier(walletOperationId, nameof(walletOperationId));
        if (pricePaidCoins <= 0 ||
            bonusSpentCoins < 0 ||
            paidSpentCoins < 0 ||
            checked(bonusSpentCoins + paidSpentCoins) != pricePaidCoins ||
            walletPaidAvailableAfter < 0 ||
            walletBonusAvailableAfter < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pricePaidCoins),
                "Unlock price, spend split and Wallet snapshot must be consistent.");
        }

        if (unlockedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Unlock time must use the UTC offset.", nameof(unlockedAt));
        }

        return new ChapterUnlock
        {
            Id = id,
            UserId = userId,
            ChapterId = chapterId,
            QuoteId = quoteId,
            WalletOperationId = walletOperationId,
            PricePaidCoins = pricePaidCoins,
            BonusSpentCoins = bonusSpentCoins,
            PaidSpentCoins = paidSpentCoins,
            WalletPaidAvailableAfter = walletPaidAvailableAfter,
            WalletBonusAvailableAfter = walletBonusAvailableAfter,
            UnlockedAt = unlockedAt,
        };
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", parameterName);
        }
    }
}

public enum UnlockChapterOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
    QuoteExpired = 3,
    PriceChanged = 4,
    UnlockNotRequired = 5,
    InsufficientCoins = 6,
    IdempotencyConflict = 7,
    InvariantViolation = 8,
}

public sealed record UnlockChapterState(
    Guid ChapterId,
    long BonusCoins,
    long PaidCoins,
    long PaidAvailableAfter,
    long BonusAvailableAfter,
    bool AlreadyUnlocked);

public sealed record UnlockChapterResult(
    UnlockChapterOutcome Outcome,
    UnlockChapterState? State = null);

public interface IChapterUnlockStore
{
    Task<UnlockChapterResult> UnlockAsync(
        Guid userId,
        Guid chapterId,
        Guid quoteId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}

public sealed class ChapterUnlockHandler(IChapterUnlockStore store)
{
    public async Task<UnlockChapterResult> UnlockAsync(
        Guid userId,
        Guid chapterId,
        Guid quoteId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            chapterId == Guid.Empty ||
            quoteId == Guid.Empty ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            !string.Equals(idempotencyKey, idempotencyKey.Trim(), StringComparison.Ordinal) ||
            idempotencyKey.Length > 200)
        {
            return new UnlockChapterResult(UnlockChapterOutcome.ValidationFailed);
        }

        return await store.UnlockAsync(
            userId,
            chapterId,
            quoteId,
            idempotencyKey,
            cancellationToken);
    }
}
