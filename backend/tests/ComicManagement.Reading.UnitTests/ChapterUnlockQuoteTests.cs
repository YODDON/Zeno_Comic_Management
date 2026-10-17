using ComicManagement.Reading.Access;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Reading.UnitTests;

public sealed class ChapterUnlockQuoteTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Active_vip_discount_rounds_up_and_estimates_bonus_before_paid()
    {
        var userId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var store = new FakeStore(new ChapterUnlockQuoteSource(
            new ChapterAccessProjection(
                chapterId,
                101,
                VipEligible: false,
                HasActiveVip: true,
                HasCoinUnlock: false,
                ActiveVipDiscountPercent: 20),
            PaidAvailable: 100,
            BonusAvailable: 30));

        var result = await new ChapterUnlockQuoteHandler(store, new FakeClock())
            .CreateAsync(userId, chapterId);

        Assert.Equal(ChapterUnlockQuoteOutcome.Succeeded, result.Outcome);
        Assert.Equal(101, result.Quote!.BasePriceCoins);
        Assert.True(result.Quote.VipDiscountApplied);
        Assert.Equal(81, result.Quote.FinalPriceCoins);
        Assert.Equal(30, result.Quote.BonusToSpend);
        Assert.Equal(51, result.Quote.PaidToSpend);
        Assert.Equal(Now.AddMinutes(5), result.Quote.ExpiresAt);
        Assert.Matches("^[0-9a-f]{64}$", result.Quote.PriceRevision);
        Assert.NotNull(store.Added);
    }

    [Fact]
    public async Task Quote_does_not_claim_unavailable_wallet_funds()
    {
        var userId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var store = new FakeStore(new ChapterUnlockQuoteSource(
            new ChapterAccessProjection(chapterId, 100, false, false, false),
            PaidAvailable: 20,
            BonusAvailable: 10));

        var result = await new ChapterUnlockQuoteHandler(store, new FakeClock())
            .CreateAsync(userId, chapterId);

        Assert.Equal(100, result.Quote!.FinalPriceCoins);
        Assert.Equal(10, result.Quote.BonusToSpend);
        Assert.Equal(20, result.Quote.PaidToSpend);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(100, true, false)]
    [InlineData(100, false, true)]
    public async Task Accessible_chapter_does_not_create_quote(
        long price,
        bool vipEligible,
        bool unlocked)
    {
        var userId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var store = new FakeStore(new ChapterUnlockQuoteSource(
            new ChapterAccessProjection(
                chapterId,
                price,
                vipEligible,
                HasActiveVip: vipEligible,
                HasCoinUnlock: unlocked,
                ActiveVipDiscountPercent: vipEligible ? 20 : 0),
            0,
            0));

        var result = await new ChapterUnlockQuoteHandler(store, new FakeClock())
            .CreateAsync(userId, chapterId);

        Assert.Equal(ChapterUnlockQuoteOutcome.UnlockNotRequired, result.Outcome);
        Assert.Null(store.Added);
    }

    [Fact]
    public async Task Missing_or_invalid_identity_fails_without_persistence()
    {
        var store = new FakeStore(null);
        var handler = new ChapterUnlockQuoteHandler(store, new FakeClock());

        Assert.Equal(
            ChapterUnlockQuoteOutcome.NotFound,
            (await handler.CreateAsync(Guid.NewGuid(), Guid.NewGuid())).Outcome);
        Assert.Equal(
            ChapterUnlockQuoteOutcome.NotFound,
            (await handler.CreateAsync(Guid.Empty, Guid.NewGuid())).Outcome);
        Assert.Null(store.Added);
    }

    [Fact]
    public void Revision_changes_only_when_pricing_inputs_change()
    {
        var chapterId = Guid.NewGuid();
        var original = ChapterUnlockPricingPolicy.CalculateRevision(chapterId, 101, 20);

        Assert.Equal(
            original,
            ChapterUnlockPricingPolicy.CalculateRevision(chapterId, 101, 20));
        Assert.NotEqual(
            original,
            ChapterUnlockPricingPolicy.CalculateRevision(chapterId, 102, 20));
        Assert.NotEqual(
            original,
            ChapterUnlockPricingPolicy.CalculateRevision(chapterId, 101, 0));
    }

    private sealed class FakeStore(ChapterUnlockQuoteSource? source)
        : IChapterUnlockQuoteStore
    {
        internal ChapterUnlockQuote? Added { get; private set; }

        public Task<ChapterUnlockQuoteSource?> GetSourceAsync(
            Guid userId,
            Guid chapterId,
            CancellationToken cancellationToken = default) => Task.FromResult(source);

        public Task AddAsync(
            ChapterUnlockQuote quote,
            CancellationToken cancellationToken = default)
        {
            Added = quote;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
