namespace ComicManagement.Membership.Subscriptions;

public sealed class VipTransaction
{
    public const int MaximumPaymentOrderTypeLength = 32;

    private VipTransaction()
    {
    }

    public Guid Id { get; private set; }

    public Guid PaymentOrderId { get; private set; }

    public Guid UserId { get; private set; }

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
        if (paymentOrderType.Length > MaximumPaymentOrderTypeLength ||
            durationHours <= 0 ||
            fulfilledAt.Offset != TimeSpan.Zero ||
            extension.EffectiveStart < fulfilledAt ||
            extension.NewExpiry != extension.EffectiveStart.AddHours(durationHours))
        {
            throw new ArgumentException("VIP transaction state is invalid.");
        }

        return new VipTransaction
        {
            Id = id,
            PaymentOrderId = paymentOrderId,
            UserId = userId,
            PaymentOrderType = paymentOrderType,
            PreviousExpiry = extension.PreviousExpiry,
            EffectiveStart = extension.EffectiveStart,
            NewExpiry = extension.NewExpiry,
            DurationHours = durationHours,
            FulfilledAt = fulfilledAt,
            CreatedAt = fulfilledAt,
        };
    }
}
