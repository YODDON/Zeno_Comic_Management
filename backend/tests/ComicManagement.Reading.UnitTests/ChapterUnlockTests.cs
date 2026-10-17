using ComicManagement.Reading.Unlocks;
using Xunit;

namespace ComicManagement.Reading.UnitTests;

public sealed class ChapterUnlockTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 17, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_requires_a_consistent_spend_and_wallet_snapshot()
    {
        var unlock = ChapterUnlock.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            80,
            30,
            50,
            50,
            0,
            Now);

        Assert.Equal(80, unlock.PricePaidCoins);
        Assert.Equal(30, unlock.BonusSpentCoins);
        Assert.Equal(50, unlock.PaidSpentCoins);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChapterUnlock.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            80,
            30,
            49,
            50,
            0,
            Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" key")]
    [InlineData("key ")]
    public async Task Handler_rejects_invalid_idempotency_key(string key)
    {
        var store = new FakeStore();
        var result = await new ChapterUnlockHandler(store).UnlockAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            key);

        Assert.Equal(UnlockChapterOutcome.ValidationFailed, result.Outcome);
        Assert.False(store.Called);
    }

    [Fact]
    public async Task Handler_forwards_a_valid_command()
    {
        var store = new FakeStore();
        var result = await new ChapterUnlockHandler(store).UnlockAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "unlock-1");

        Assert.Equal(UnlockChapterOutcome.Succeeded, result.Outcome);
        Assert.True(store.Called);
    }

    private sealed class FakeStore : IChapterUnlockStore
    {
        internal bool Called { get; private set; }

        public Task<UnlockChapterResult> UnlockAsync(
            Guid userId,
            Guid chapterId,
            Guid quoteId,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new UnlockChapterResult(
                UnlockChapterOutcome.Succeeded,
                new UnlockChapterState(chapterId, 1, 1, 0, 0, false)));
        }
    }
}
