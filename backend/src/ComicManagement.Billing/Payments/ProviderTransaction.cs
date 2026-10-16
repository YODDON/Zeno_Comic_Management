namespace ComicManagement.Billing.Payments;

public static class ProviderTransactionDirections
{
    public const string Incoming = "Incoming";

    public const string Outgoing = "Outgoing";
}

public static class ProviderTransactionProcessingStatuses
{
    public const string Received = "Received";

    public const string ReconciliationRequired = "ReconciliationRequired";

    public const string Fulfilled = "Fulfilled";
}

public sealed class ProviderTransaction
{
    public const int MaximumProviderLength = 32;
    public const int MaximumProviderTransactionIdLength = 128;
    public const int PayloadFingerprintLength = 64;
    public const int MaximumDirectionLength = 16;
    public const int MaximumPaymentCodeLength = 64;
    public const int MaximumAccountNumberLast4Length = 4;
    public const int MaximumReferenceCodeLength = 128;
    public const int MaximumProcessingStatusLength = 32;
    public const int MaximumReconciliationReasonLength = 64;

    private ProviderTransaction()
    {
    }

    public Guid Id { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string ProviderTransactionId { get; private set; } = string.Empty;

    public string PayloadFingerprint { get; private set; } = string.Empty;

    public long AmountVnd { get; private set; }

    public string Direction { get; private set; } = string.Empty;

    public string? PaymentCode { get; private set; }

    public string AccountNumberLast4 { get; private set; } = string.Empty;

    public string? ReferenceCode { get; private set; }

    public Guid? MatchedOrderId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset VerifiedAt { get; private set; }

    public string ProcessingStatus { get; private set; } = string.Empty;

    public string? ReconciliationReason { get; private set; }

    public static ProviderTransaction CreateSePay(
        Guid id,
        SePayNormalizedTransaction normalized,
        DateTimeOffset verifiedAt)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", nameof(id));
        }

        EnsureUtc(verifiedAt, nameof(verifiedAt));
        EnsureUtc(normalized.OccurredAt, nameof(normalized));

        return new ProviderTransaction
        {
            Id = id,
            Provider = PaymentProviders.SePay,
            ProviderTransactionId = normalized.ProviderTransactionId,
            PayloadFingerprint = normalized.PayloadFingerprint,
            AmountVnd = normalized.AmountVnd,
            Direction = normalized.Direction,
            PaymentCode = normalized.PaymentCode,
            AccountNumberLast4 = normalized.AccountNumberLast4,
            ReferenceCode = normalized.ReferenceCode,
            OccurredAt = normalized.OccurredAt,
            VerifiedAt = verifiedAt,
            ProcessingStatus = normalized.ReconciliationReason is null
                ? ProviderTransactionProcessingStatuses.Received
                : ProviderTransactionProcessingStatuses.ReconciliationRequired,
            ReconciliationReason = normalized.ReconciliationReason,
        };
    }

    public void RequireReconciliation(string reason, Guid? matchedOrderId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaximumReconciliationReasonLength)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (matchedOrderId == Guid.Empty)
        {
            throw new ArgumentException("A matched order identifier cannot be empty.", nameof(matchedOrderId));
        }

        if (ProcessingStatus == ProviderTransactionProcessingStatuses.Fulfilled)
        {
            throw new InvalidOperationException(
                "A fulfilled provider transaction cannot be moved to reconciliation.");
        }

        if (matchedOrderId is not null &&
            MatchedOrderId is not null &&
            MatchedOrderId != matchedOrderId)
        {
            throw new InvalidOperationException(
                "A provider transaction cannot be matched to a different order.");
        }

        MatchedOrderId ??= matchedOrderId;
        ProcessingStatus = ProviderTransactionProcessingStatuses.ReconciliationRequired;
        ReconciliationReason = reason;
    }

    public void MarkFulfilled(Guid matchedOrderId)
    {
        if (matchedOrderId == Guid.Empty)
        {
            throw new ArgumentException("A matched order identifier is required.", nameof(matchedOrderId));
        }

        if (ProcessingStatus != ProviderTransactionProcessingStatuses.Received ||
            MatchedOrderId is not null)
        {
            throw new InvalidOperationException(
                "Only a received, unmatched provider transaction can be fulfilled.");
        }

        MatchedOrderId = matchedOrderId;
        ProcessingStatus = ProviderTransactionProcessingStatuses.Fulfilled;
        ReconciliationReason = null;
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }
}
