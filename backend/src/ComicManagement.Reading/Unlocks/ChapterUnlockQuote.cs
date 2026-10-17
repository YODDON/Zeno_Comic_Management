using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ComicManagement.Reading.Access;
using ComicManagement.Shared.Time;

namespace ComicManagement.Reading.Unlocks;

public static class ChapterUnlockPricingPolicy
{
    public const int VipDiscountPercent = 20;
    public const string Revision = "u01-v1";

    public static long CalculateFinalPrice(
        long basePriceCoins,
        int discountPercent)
    {
        if (basePriceCoins <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(basePriceCoins));
        }

        if (discountPercent is not (0 or VipDiscountPercent))
        {
            throw new ArgumentOutOfRangeException(nameof(discountPercent));
        }

        var multiplier = 100 - discountPercent;
        return checked((long)decimal.Ceiling(basePriceCoins * (decimal)multiplier / 100m));
    }

    public static string CalculateRevision(
        Guid chapterId,
        long basePriceCoins,
        int discountPercent)
    {
        if (chapterId == Guid.Empty)
        {
            throw new ArgumentException("A chapter identifier is required.", nameof(chapterId));
        }

        var finalPriceCoins = CalculateFinalPrice(basePriceCoins, discountPercent);
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{Revision}|{chapterId:N}|{basePriceCoins}|{discountPercent}|{finalPriceCoins}");
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed class ChapterUnlockQuote
{
    public const int PriceRevisionLength = 64;
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private ChapterUnlockQuote()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid ChapterId { get; private set; }

    public long BasePriceCoins { get; private set; }

    public bool VipDiscountApplied { get; private set; }

    public int VipDiscountPercent { get; private set; }

    public long FinalPriceCoins { get; private set; }

    public long BonusToSpend { get; private set; }

    public long PaidToSpend { get; private set; }

    public string PriceRevision { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ChapterUnlockQuote Create(
        Guid id,
        Guid userId,
        Guid chapterId,
        long basePriceCoins,
        int vipDiscountPercent,
        long bonusAvailable,
        long paidAvailable,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || userId == Guid.Empty || chapterId == Guid.Empty)
        {
            throw new ArgumentException("Quote, user and chapter identifiers are required.");
        }

        if (bonusAvailable < 0 || paidAvailable < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bonusAvailable),
                "Wallet availability cannot be negative.");
        }

        if (createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Quote time must use the UTC offset.", nameof(createdAt));
        }

        var finalPriceCoins = ChapterUnlockPricingPolicy.CalculateFinalPrice(
            basePriceCoins,
            vipDiscountPercent);
        var priceRevision = ChapterUnlockPricingPolicy.CalculateRevision(
            chapterId,
            basePriceCoins,
            vipDiscountPercent);
        var bonusToSpend = Math.Min(bonusAvailable, finalPriceCoins);
        var paidToSpend = Math.Min(paidAvailable, finalPriceCoins - bonusToSpend);

        return new ChapterUnlockQuote
        {
            Id = id,
            UserId = userId,
            ChapterId = chapterId,
            BasePriceCoins = basePriceCoins,
            VipDiscountApplied = vipDiscountPercent > 0,
            VipDiscountPercent = vipDiscountPercent,
            FinalPriceCoins = finalPriceCoins,
            BonusToSpend = bonusToSpend,
            PaidToSpend = paidToSpend,
            PriceRevision = priceRevision,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(TimeToLive),
        };
    }
}

public enum ChapterUnlockQuoteOutcome
{
    Succeeded = 0,
    NotFound = 1,
    UnlockNotRequired = 2,
}

public sealed record ChapterUnlockQuoteSource(
    ChapterAccessProjection Access,
    long PaidAvailable,
    long BonusAvailable);

public sealed record ChapterUnlockQuoteState(
    Guid QuoteId,
    Guid ChapterId,
    long BasePriceCoins,
    bool VipDiscountApplied,
    long FinalPriceCoins,
    long BonusToSpend,
    long PaidToSpend,
    DateTimeOffset ExpiresAt,
    string PriceRevision);

public sealed record ChapterUnlockQuoteResult(
    ChapterUnlockQuoteOutcome Outcome,
    ChapterUnlockQuoteState? Quote = null);

public interface IChapterUnlockQuoteStore
{
    Task<ChapterUnlockQuoteSource?> GetSourceAsync(
        Guid userId,
        Guid chapterId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        ChapterUnlockQuote quote,
        CancellationToken cancellationToken = default);
}

public sealed class ChapterUnlockQuoteHandler(
    IChapterUnlockQuoteStore store,
    IClock clock)
{
    public async Task<ChapterUnlockQuoteResult> CreateAsync(
        Guid userId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || chapterId == Guid.Empty)
        {
            return new ChapterUnlockQuoteResult(ChapterUnlockQuoteOutcome.NotFound);
        }

        var source = await store.GetSourceAsync(userId, chapterId, cancellationToken);
        if (source is null)
        {
            return new ChapterUnlockQuoteResult(ChapterUnlockQuoteOutcome.NotFound);
        }

        var access = ChapterAccessPolicy.Resolve(source.Access, isAuthenticated: true);
        if (access.AccessType != ChapterAccessTypes.Locked)
        {
            return new ChapterUnlockQuoteResult(ChapterUnlockQuoteOutcome.UnlockNotRequired);
        }

        var discountPercent = source.Access.HasActiveVip && !source.Access.VipEligible
            ? source.Access.ActiveVipDiscountPercent
            : 0;
        var quote = ChapterUnlockQuote.Create(
            Guid.NewGuid(),
            userId,
            chapterId,
            source.Access.EffectivePriceCoins,
            discountPercent,
            source.BonusAvailable,
            source.PaidAvailable,
            clock.UtcNow);
        await store.AddAsync(quote, cancellationToken);

        return new ChapterUnlockQuoteResult(
            ChapterUnlockQuoteOutcome.Succeeded,
            new ChapterUnlockQuoteState(
                quote.Id,
                quote.ChapterId,
                quote.BasePriceCoins,
                quote.VipDiscountApplied,
                quote.FinalPriceCoins,
                quote.BonusToSpend,
                quote.PaidToSpend,
                quote.ExpiresAt,
                quote.PriceRevision));
    }
}
