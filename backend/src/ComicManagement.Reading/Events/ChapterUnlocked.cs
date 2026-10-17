namespace ComicManagement.Reading.Events;

public static partial class ReadingEventTypes
{
    public const string ChapterUnlocked = "Reading.ChapterUnlocked.v1";
}

public sealed record ChapterUnlocked(
    Guid UnlockId,
    Guid UserId,
    Guid ChapterId,
    Guid QuoteId,
    Guid WalletOperationId,
    long PricePaidCoins,
    long BonusSpentCoins,
    long PaidSpentCoins,
    DateTimeOffset UnlockedAt);
