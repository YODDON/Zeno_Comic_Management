using ComicManagement.Billing.Payments;
using Xunit;

namespace ComicManagement.Billing.UnitTests;

public sealed class VipPaymentOrderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 17, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Vip_order_snapshots_plan_policy_and_has_no_coin_grant()
    {
        var planId = Guid.NewGuid();

        var order = CreateOrder(planId, Now.AddMinutes(30));

        Assert.Equal(PaymentOrderTypes.VipPurchase, order.OrderType);
        Assert.Equal(29_000, order.AmountVnd);
        Assert.Null(order.PaidCoinsSnapshot);
        Assert.Equal(planId, order.VipPlanId);
        Assert.Equal(720, order.VipDurationHoursSnapshot);
        Assert.Equal(20, order.VipDiscountPercentSnapshot);
    }

    [Fact]
    public void Vip_fulfillment_is_one_time_and_rejects_expiry_boundary()
    {
        var order = CreateOrder(Guid.NewGuid(), Now.AddMinutes(30));

        order.FulfillVipPurchase(Now);

        Assert.Equal(TopUpOrderStatuses.Succeeded, order.OrderStatus);
        Assert.Equal(Now, order.VerifiedPaidAt);
        Assert.Equal(Now, order.FulfilledAt);
        Assert.Throws<InvalidOperationException>(() => order.FulfillVipPurchase(Now));

        var expiring = CreateOrder(Guid.NewGuid(), Now);
        Assert.Throws<InvalidOperationException>(() => expiring.FulfillVipPurchase(Now));
        Assert.True(expiring.ExpireIfDue(Now));
        Assert.False(expiring.ExpireIfDue(Now.AddHours(-1)));
        Assert.Equal(TopUpOrderStatuses.Expired, expiring.OrderStatus);
    }

    [Theory]
    [InlineData(719, 20)]
    [InlineData(721, 20)]
    [InlineData(720, 19)]
    [InlineData(720, 21)]
    public void Vip_order_rejects_policy_outside_fixed_MVP_values(
        int durationHours,
        int discountPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PaymentOrder.CreateVipPurchase(
                Guid.NewGuid(),
                Guid.NewGuid(),
                $"ZENO-VIP-{Guid.NewGuid():N}",
                29_000,
                Guid.NewGuid(),
                durationHours,
                discountPercent,
                "vip-invalid-policy",
                new string('b', PaymentOrder.RequestFingerprintLength),
                Now,
                Now.AddMinutes(30)));
    }

    private static PaymentOrder CreateOrder(Guid planId, DateTimeOffset expiresAt) =>
        PaymentOrder.CreateVipPurchase(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"ZENO-VIP-{Guid.NewGuid():N}",
            29_000,
            planId,
            720,
            20,
            "vip-key",
            new string('a', 64),
            expiresAt.AddMinutes(-30),
            expiresAt);
}
