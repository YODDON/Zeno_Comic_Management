using ComicManagement.Billing.Payments;
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
        Assert.Equal(TimeSpan.FromMinutes(45), store.LastRequest!.TimeToLive);
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
        existing.ExpireIfDue(Now);
        var store = new StubStore(existing);
        var handler = CreateHandler(store);

        var result = await handler.CreateAsync(existing.UserId, 10_000, "same-key");

        Assert.Equal(TopUpOrderOutcome.Succeeded, result.Outcome);
        Assert.Equal(existing.Id, result.State!.OrderId);
        Assert.Equal(TopUpOrderStatuses.Expired, result.State.Status);
    }

    [Fact]
    public void Expiry_is_monotonic_even_if_the_clock_moves_backwards()
    {
        var order = PaymentOrder.CreateTopUp(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ZENO-MONOTONIC",
            10_000,
            1_000,
            "monotonic-key",
            new string('b', 64),
            Now.AddMinutes(-31),
            Now.AddMinutes(-1));

        Assert.True(order.ExpireIfDue(Now));
        var expiredAt = order.ExpiredAt;
        var version = order.Version;

        Assert.False(order.ExpireIfDue(Now.AddHours(-1)));
        Assert.Equal(TopUpOrderStatuses.Expired, order.OrderStatus);
        Assert.Equal(expiredAt, order.ExpiredAt);
        Assert.Equal(version, order.Version);
    }

    [Fact]
    public void Options_default_to_thirty_minutes_and_policy_rejects_non_positive_ttl()
    {
        Assert.Equal(30, new TopUpOrderOptions().TopUpOrderTtlMinutes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TopUpOrderPolicy(TimeSpan.Zero));
    }

    [Fact]
    public void Fulfillment_atomically_closes_the_pending_order_lifecycle()
    {
        var order = CreatePendingOrder(Now.AddMinutes(30));

        order.FulfillTopUp(Now);

        Assert.Equal(TopUpOrderStatuses.Succeeded, order.OrderStatus);
        Assert.Equal(ProviderPaymentStatuses.Verified, order.ProviderPaymentStatus);
        Assert.Equal(PaymentFulfillmentStatuses.Fulfilled, order.FulfillmentStatus);
        Assert.Equal(Now, order.VerifiedPaidAt);
        Assert.Equal(Now, order.FulfilledAt);
        Assert.False(order.ExpireIfDue(Now.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => order.FulfillTopUp(Now));
    }

    [Fact]
    public void Fulfillment_rejects_authoritative_time_at_or_after_expiry()
    {
        var order = CreatePendingOrder(Now);

        Assert.Throws<InvalidOperationException>(() => order.FulfillTopUp(Now));
        Assert.Equal(TopUpOrderStatuses.Pending, order.OrderStatus);
        Assert.True(order.ExpireIfDue(Now));
        Assert.Throws<InvalidOperationException>(() => order.FulfillTopUp(Now));
    }

    [Fact]
    public void Fulfilled_provider_transaction_cannot_be_rematched_or_reconciled()
    {
        var orderId = Guid.NewGuid();
        var providerTransaction = ProviderTransaction.CreateSePay(
            Guid.NewGuid(),
            new SePayNormalizedTransaction(
                "provider-1",
                new string('c', 64),
                10_000,
                ProviderTransactionDirections.Incoming,
                "ZENO-ORDER",
                "6789",
                "REF-1",
                Now.AddMinutes(-1),
                null),
            Now);

        providerTransaction.MarkFulfilled(orderId);

        Assert.Equal(
            ProviderTransactionProcessingStatuses.Fulfilled,
            providerTransaction.ProcessingStatus);
        Assert.Equal(orderId, providerTransaction.MatchedOrderId);
        Assert.Throws<InvalidOperationException>(() =>
            providerTransaction.MarkFulfilled(orderId));
        Assert.Throws<InvalidOperationException>(() =>
            providerTransaction.RequireReconciliation(
                SePayReconciliationReasons.ProviderTransactionConflict));
    }

    private static PaymentOrder CreatePendingOrder(DateTimeOffset expiresAt) =>
        PaymentOrder.CreateTopUp(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"ZENO-{Guid.NewGuid():N}",
            10_000,
            1_000,
            "fulfillment-key",
            new string('d', 64),
            expiresAt.AddMinutes(-30),
            expiresAt);

    private static TopUpOrderHandler CreateHandler(
        StubStore store,
        TimeSpan? ttl = null) => new(
            store,
            new TopUpOrderPolicy(ttl ?? TimeSpan.FromMinutes(30)));

    private sealed class StubStore(PaymentOrder? replay = null) : ITopUpOrderStore
    {
        internal int CallCount { get; private set; }

        internal CreateTopUpOrderStoreRequest? LastRequest { get; private set; }

        public Task<TopUpOrderStoreResult> CreateOrReplayAsync(
            CreateTopUpOrderStoreRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            var candidate = PaymentOrder.CreateTopUp(
                request.OrderId,
                request.UserId,
                $"ZENO-{request.OrderId:N}",
                request.AmountVnd,
                request.PaidCoins,
                request.IdempotencyKey,
                request.RequestFingerprint,
                Now,
                Now.Add(request.TimeToLive));
            return Task.FromResult(new TopUpOrderStoreResult(
                TopUpOrderOutcome.Succeeded,
                replay ?? candidate));
        }
    }
}
