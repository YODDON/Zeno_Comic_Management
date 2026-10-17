using ComicManagement.Membership.Memberships;
using ComicManagement.Membership.Plans;
using ComicManagement.Membership.Subscriptions;
using Xunit;

namespace ComicManagement.Membership.UnitTests;

public sealed class VipMembershipTests
{
    private static readonly DateTimeOffset FulfilledAt =
        new(2026, 10, 17, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Initial_plan_has_the_frozen_MVP_policy()
    {
        var plan = VipPlan.CreateInitial(FulfilledAt);

        Assert.Equal(29_000, plan.PriceVnd);
        Assert.Equal(720, plan.DurationHours);
        Assert.Equal(20, plan.DiscountPercent);
        Assert.Equal(1, plan.Version);
    }

    [Fact]
    public void Price_change_only_changes_future_price_and_version()
    {
        var plan = VipPlan.CreateInitial(FulfilledAt);

        plan.ChangeFutureOrderPrice(35_000, 1, FulfilledAt.AddMinutes(1));

        Assert.Equal(35_000, plan.PriceVnd);
        Assert.Equal(720, plan.DurationHours);
        Assert.Equal(20, plan.DiscountPercent);
        Assert.Equal(2, plan.Version);
        Assert.Throws<VipPlanConcurrencyException>(() =>
            plan.ChangeFutureOrderPrice(36_000, 1, FulfilledAt.AddMinutes(2)));
    }

    [Fact]
    public void First_purchase_starts_at_fulfillment_and_adds_exactly_720_hours()
    {
        var (subscription, extension) = VipSubscription.Create(
            Guid.NewGuid(),
            FulfilledAt,
            VipPlan.FixedDurationHours);

        Assert.Null(extension.PreviousExpiry);
        Assert.Equal(FulfilledAt, extension.EffectiveStart);
        Assert.Equal(FulfilledAt.AddHours(720), extension.NewExpiry);
        Assert.Equal(extension.NewExpiry, subscription.ExpiresAt);
    }

    [Fact]
    public void Active_renewal_extends_from_existing_expiry()
    {
        var (subscription, first) = VipSubscription.Create(
            Guid.NewGuid(),
            FulfilledAt,
            VipPlan.FixedDurationHours);

        var second = subscription.Extend(
            FulfilledAt.AddHours(1),
            VipPlan.FixedDurationHours);

        Assert.Equal(first.NewExpiry, second.PreviousExpiry);
        Assert.Equal(first.NewExpiry, second.EffectiveStart);
        Assert.Equal(FulfilledAt.AddHours(1_440), second.NewExpiry);
    }

    [Fact]
    public void Expired_renewal_starts_at_new_fulfillment_time()
    {
        var (subscription, _) = VipSubscription.Create(
            Guid.NewGuid(),
            FulfilledAt,
            VipPlan.FixedDurationHours);
        var renewalAt = subscription.ExpiresAt.AddMinutes(1);

        var renewal = subscription.Extend(renewalAt, VipPlan.FixedDurationHours);

        Assert.Equal(renewalAt, renewal.EffectiveStart);
        Assert.Equal(renewalAt.AddHours(720), renewal.NewExpiry);
    }

    [Fact]
    public void Renewal_version_is_monotonic_even_when_clock_moves_back()
    {
        var userId = Guid.NewGuid();
        var (subscription, first) = VipSubscription.Create(
            userId,
            FulfilledAt,
            VipPlan.FixedDurationHours);

        var second = subscription.Extend(
            FulfilledAt.AddMinutes(-1),
            VipPlan.FixedDurationHours);

        Assert.Equal(2, subscription.Version);
        Assert.Equal(first.NewExpiry, second.PreviousExpiry);
        Assert.Equal(first.NewExpiry, second.EffectiveStart);
        Assert.Equal(first.NewExpiry.AddHours(720), second.NewExpiry);
    }

    [Theory]
    [InlineData("TopUp", 1, 720)]
    [InlineData("VipPurchase", 0, 720)]
    [InlineData("VipPurchase", 1, 719)]
    [InlineData("VipPurchase", 2, 720)]
    public void Vip_transaction_rejects_invalid_order_policy_or_sequence(
        string orderType,
        long subscriptionVersion,
        int durationHours)
    {
        var extension = new VipExtension(
            PreviousExpiry: null,
            FulfilledAt,
            FulfilledAt.AddHours(durationHours));

        Assert.Throws<ArgumentException>(() => VipTransaction.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            orderType,
            subscriptionVersion,
            extension,
            durationHours,
            FulfilledAt));
    }

    [Fact]
    public async Task Checkout_rejects_invalid_identifiers_and_keys()
    {
        var store = new StubStore();
        var handler = new VipMembershipHandler(
            store,
            new VipOrderPolicy(TimeSpan.FromMinutes(30)));

        var result = await handler.CheckoutAsync(
            Guid.Empty,
            VipPlan.InitialPlanId,
            "key");

        Assert.Equal(VipMembershipOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.CheckoutCalls);
    }

    [Fact]
    public async Task Checkout_uses_a_stable_fingerprint_and_configured_ttl()
    {
        var store = new StubStore();
        var handler = new VipMembershipHandler(
            store,
            new VipOrderPolicy(TimeSpan.FromMinutes(45)));
        var userId = Guid.NewGuid();

        await handler.CheckoutAsync(userId, VipPlan.InitialPlanId, "vip-key");
        var first = Assert.IsType<CreateVipCheckoutStoreRequest>(store.LastCheckout);
        await handler.CheckoutAsync(userId, VipPlan.InitialPlanId, "vip-key");
        var second = Assert.IsType<CreateVipCheckoutStoreRequest>(store.LastCheckout);

        Assert.Equal(TimeSpan.FromMinutes(45), first.TimeToLive);
        Assert.Equal(first.RequestFingerprint, second.RequestFingerprint);
        Assert.Equal(64, first.RequestFingerprint.Length);
    }

    private sealed class StubStore : IVipMembershipStore
    {
        internal int CheckoutCalls { get; private set; }

        internal CreateVipCheckoutStoreRequest? LastCheckout { get; private set; }

        public Task<VipMembershipResult> GetPlanAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<VipMembershipResult> GetMembershipAsync(
            Guid userId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<VipMembershipResult> CreateOrReplayCheckoutAsync(
            CreateVipCheckoutStoreRequest request,
            CancellationToken cancellationToken = default)
        {
            CheckoutCalls++;
            LastCheckout = request;
            return Task.FromResult(new VipMembershipResult(VipMembershipOutcome.Succeeded));
        }

        public Task<VipMembershipResult> UpdateFutureOrderPriceAsync(
            UpdateVipPlanPriceStoreRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
