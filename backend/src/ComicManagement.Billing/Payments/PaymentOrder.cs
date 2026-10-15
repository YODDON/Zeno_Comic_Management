namespace ComicManagement.Billing.Payments;

public static class PaymentOrderTypes
{
    public const string TopUp = "TopUp";

    public const string VipPurchase = "VipPurchase";
}

public static class PaymentProviders
{
    public const string SePay = "SePay";
}

public static class ProviderPaymentStatuses
{
    public const string Pending = "Pending";

    public const string Verified = "Verified";

    public const string Rejected = "Rejected";

    public const string ReconciliationRequired = "ReconciliationRequired";
}

public static class PaymentFulfillmentStatuses
{
    public const string NotStarted = "NotStarted";

    public const string Retryable = "Retryable";

    public const string Fulfilled = "Fulfilled";

    public const string ManualReview = "ManualReview";
}

public static class TopUpOrderStatuses
{
    public const string Pending = "Pending";

    public const string Expired = "Expired";
}

public sealed class PaymentOrder
{
    public const int MaximumOrderCodeLength = 64;
    public const int MaximumTypeLength = 32;
    public const int MaximumProviderLength = 32;
    public const int MaximumStatusLength = 32;
    public const int MaximumIdempotencyKeyLength = 200;
    public const int RequestFingerprintLength = 64;

    private PaymentOrder()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string OrderCode { get; private set; } = string.Empty;

    public string OrderType { get; private set; } = string.Empty;

    public long AmountVnd { get; private set; }

    public long? PaidCoinsSnapshot { get; private set; }

    public Guid? VipPlanId { get; private set; }

    public int? VipDurationHoursSnapshot { get; private set; }

    public int? VipDiscountPercentSnapshot { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string ProviderPaymentStatus { get; private set; } = string.Empty;

    public string FulfillmentStatus { get; private set; } = string.Empty;

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string RequestFingerprint { get; private set; } = string.Empty;

    public string OrderStatus { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ExpiredAt { get; private set; }

    public DateTimeOffset? VerifiedPaidAt { get; private set; }

    public DateTimeOffset? FulfilledAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public long Version { get; private set; }

    public static PaymentOrder CreateTopUp(
        Guid id,
        Guid userId,
        string orderCode,
        long amountVnd,
        long paidCoins,
        string idempotencyKey,
        string requestFingerprint,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        EnsureIdentifier(id, nameof(id));
        EnsureIdentifier(userId, nameof(userId));
        EnsureText(orderCode, MaximumOrderCodeLength, nameof(orderCode));
        TopUpAmountPolicy.EnsureValid(amountVnd);
        if (paidCoins != TopUpAmountPolicy.CalculatePaidCoins(amountVnd))
        {
            throw new ArgumentException(
                "Paid Coins must match the TopUp conversion snapshot.",
                nameof(paidCoins));
        }

        EnsureText(idempotencyKey, MaximumIdempotencyKeyLength, nameof(idempotencyKey));
        EnsureFingerprint(requestFingerprint);
        EnsureUtc(createdAt, nameof(createdAt));
        EnsureUtc(expiresAt, nameof(expiresAt));
        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                "Payment order expiry must be after creation time.");
        }

        return new PaymentOrder
        {
            Id = id,
            UserId = userId,
            OrderCode = orderCode,
            OrderType = PaymentOrderTypes.TopUp,
            AmountVnd = amountVnd,
            PaidCoinsSnapshot = paidCoins,
            Provider = PaymentProviders.SePay,
            ProviderPaymentStatus = ProviderPaymentStatuses.Pending,
            FulfillmentStatus = PaymentFulfillmentStatuses.NotStarted,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
            OrderStatus = TopUpOrderStatuses.Pending,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            Version = 1,
        };
    }

    public bool ExpireIfDue(DateTimeOffset serverNow)
    {
        EnsureUtc(serverNow, nameof(serverNow));
        if (OrderStatus != TopUpOrderStatuses.Pending ||
            ProviderPaymentStatus != ProviderPaymentStatuses.Pending ||
            serverNow < ExpiresAt)
        {
            return false;
        }

        OrderStatus = TopUpOrderStatuses.Expired;
        ExpiredAt = serverNow;
        Version++;
        return true;
    }

    private static void EnsureFingerprint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != RequestFingerprintLength ||
            value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Request fingerprint must be a lowercase SHA-256 hex digest.",
                nameof(value));
        }
    }

    private static void EnsureText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", parameterName);
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }
}
