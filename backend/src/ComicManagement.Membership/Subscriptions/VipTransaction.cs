namespace ComicManagement.Membership.Subscriptions;

public sealed class VipTransaction
{
    public const int MaximumPaymentOrderTypeLength = 32;
    public const string VipPurchaseOrderType = "VipPurchase";

    private VipTransaction()
    {
    }

    public Guid Id { get; private set; }

    public Guid PaymentOrderId { get; private set; }

    public Guid UserId { get; private set; }

    public long SubscriptionVersion { get; private set; }

    public string PaymentOrderType { get; private set; } = string.Empty;

    public DateTimeOffset? PreviousExpiry { get; private set; }

    public DateTimeOffset EffectiveStart { get; private set; }

    public DateTimeOffset NewExpiry { get; private set; }

    public int DurationHours { get; private set; }

    public DateTimeOffset FulfilledAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static VipTransaction Create(
        Guid id,
        Guid paymentOrderId,
        Guid userId,
        string paymentOrderType,
        long subscriptionVersion,
        VipExtension extension,
        int durationHours,
        DateTimeOffset fulfilledAt)
    {
        if (id == Guid.Empty || paymentOrderId == Guid.Empty || userId == Guid.Empty)
        {
            throw new ArgumentException("VIP transaction identifiers are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(paymentOrderType);
        ArgumentNullException.ThrowIfNull(extension);
        if (!string.Equals(
                paymentOrderType,
                VipPurchaseOrderType,
                StringComparison.Ordinal) ||
            subscriptionVersion <= 0 ||
            durationHours != Plans.VipPlan.FixedDurationHours ||
            fulfilledAt.Offset != TimeSpan.Zero ||
            extension.EffectiveStart < fulfilledAt ||
            extension.NewExpiry != extension.EffectiveStart.AddHours(durationHours) ||
            (subscriptionVersion == 1) != (extension.PreviousExpiry is null))
        {
            throw new ArgumentException("VIP transaction state is invalid.");
        }

        return new VipTransaction
        {
            Id = id,
            PaymentOrderId = paymentOrderId,
            UserId = userId,
            PaymentOrderType = paymentOrderType,
            SubscriptionVersion = subscriptionVersion,
            PreviousExpiry = extension.PreviousExpiry,
            EffectiveStart = extension.EffectiveStart,
            NewExpiry = extension.NewExpiry,
            DurationHours = durationHours,
            FulfilledAt = fulfilledAt,
            CreatedAt = fulfilledAt,
        };
    }
}
