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

    public void RequireReconciliation(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaximumReconciliationReasonLength)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        ProcessingStatus = ProviderTransactionProcessingStatuses.ReconciliationRequired;
        ReconciliationReason = reason;
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }
}
