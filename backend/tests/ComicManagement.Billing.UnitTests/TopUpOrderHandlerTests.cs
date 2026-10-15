using ComicManagement.Billing.Payments;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Billing.UnitTests;

public sealed class TopUpOrderHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(9_000)]
    [InlineData(10_500)]
    [InlineData(2_001_000)]
    public async Task Create_rejects_amount_outside_the_approved_range_or_step(long amountVnd)
    {
        var store = new StubStore();
        var handler = CreateHandler(store);

        var result = await handler.CreateAsync(Guid.NewGuid(), amountVnd, "topup-1");

        Assert.Equal(TopUpOrderOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.CallCount);
    }

    [Theory]
    [InlineData(10_000, 1_000)]
    [InlineData(29_000, 2_900)]
    [InlineData(2_000_000, 200_000)]
    public async Task Create_snapshots_conversion_and_uses_configured_server_ttl(
        long amountVnd,
        long expectedCoins)
    {
        var store = new StubStore();
        var handler = CreateHandler(store, TimeSpan.FromMinutes(45));

        var result = await handler.CreateAsync(Guid.NewGuid(), amountVnd, "topup-1");

        Assert.Equal(TopUpOrderOutcome.Succeeded, result.Outcome);
        Assert.Equal(expectedCoins, result.State!.PaidCoinsExpected);
        Assert.Equal(Now.AddMinutes(45), result.State.ExpiresAt);
        Assert.Equal(TopUpOrderStatuses.Pending, result.State.Status);
        Assert.Equal(Now, store.LastCandidate!.CreatedAt);
    }

    [Fact]
    public async Task Replay_after_expiry_returns_the_same_expired_order()
    {
        var existing = PaymentOrder.CreateTopUp(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ZENO-EXISTING",
            10_000,
            1_000,
            "same-key",
            new string('a', 64),
            Now.AddMinutes(-31),
            Now.AddMinutes(-1));
        var store = new StubStore(existing);
        var handler = CreateHandler(store);

        var result = await handler.CreateAsync(existing.UserId, 10_000, "same-key");

        Assert.Equal(TopUpOrderOutcome.Succeeded, result.Outcome);
        Assert.Equal(existing.Id, result.State!.OrderId);
        Assert.Equal(TopUpOrderStatuses.Expired, result.State.Status);
    }

    [Fact]
    public void Options_default_to_thirty_minutes_and_policy_rejects_non_positive_ttl()
    {
        Assert.Equal(30, new TopUpOrderOptions().TopUpOrderTtlMinutes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TopUpOrderPolicy(TimeSpan.Zero));
    }

    private static TopUpOrderHandler CreateHandler(
        StubStore store,
        TimeSpan? ttl = null) => new(
            store,
            new StubClock(Now),
            new TopUpOrderPolicy(ttl ?? TimeSpan.FromMinutes(30)));

    private sealed class StubStore(PaymentOrder? replay = null) : ITopUpOrderStore
    {
        internal int CallCount { get; private set; }

        internal PaymentOrder? LastCandidate { get; private set; }

        public Task<TopUpOrderStoreResult> CreateOrReplayAsync(
            PaymentOrder candidate,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCandidate = candidate;
            return Task.FromResult(new TopUpOrderStoreResult(
                TopUpOrderOutcome.Succeeded,
                replay ?? candidate));
        }
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
