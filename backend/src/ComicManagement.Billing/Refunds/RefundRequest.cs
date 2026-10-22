using ComicManagement.Billing.Payments;

namespace ComicManagement.Billing.Refunds;

public static class RefundRequestStatuses
{
    public const string Requested = "Requested";
    public const string UnderReview = "UnderReview";
    public const string RecoveryReserved = "RecoveryReserved";
    public const string TransferPending = "TransferPending";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
    public const string NeedsReconciliation = "NeedsReconciliation";
    public const string TransferConfirmedFailed = "TransferConfirmedFailed";
    public const string ReservationReleased = "ReservationReleased";
}

public sealed class RefundRequest
{
    public const int MaximumStatusLength = 40;
    public const int MaximumReasonLength = 1000;
    public const int MaximumIdempotencyKeyLength = 200;
    public const int FingerprintLength = 64;
    public const int MaximumSecurityVersionLength = 40;
    public const int FeePercent = 5;
    public const int RequestWindowDays = 7;

    private RefundRequest()
    {
    }

    public Guid Id { get; private set; }

    public Guid PaymentOrderId { get; private set; }

    public Guid UserId { get; private set; }

    public string PaymentOrderType { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public long OriginalAmountVnd { get; private set; }

    public long FeeAmountVnd { get; private set; }

    public long NetRefundVnd { get; private set; }

    public long RequiredRecoveryCoins { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string RequestFingerprint { get; private set; } = string.Empty;

    public string RequestFingerprintVersion { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? CompletedBy { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? DecisionReason { get; private set; }

    public long Version { get; private set; }

    public static RefundRequest Create(
        Guid id,
        Guid paymentOrderId,
        Guid userId,
        string reason,
        long originalAmountVnd,
        long requiredRecoveryCoins,
        string idempotencyKey,
        string requestFingerprint,
        string requestFingerprintVersion,
        DateTimeOffset requestedAt)
    {
        EnsureIdentifier(id, nameof(id));
        EnsureIdentifier(paymentOrderId, nameof(paymentOrderId));
        EnsureIdentifier(userId, nameof(userId));
        EnsureText(reason, MaximumReasonLength, nameof(reason));
        EnsureText(idempotencyKey, MaximumIdempotencyKeyLength, nameof(idempotencyKey));
        EnsureFingerprint(requestFingerprint);
        EnsureSecurityVersion(requestFingerprintVersion);
        EnsureUtc(requestedAt, nameof(requestedAt));
        if (originalAmountVnd <= 0 || originalAmountVnd % 1000 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalAmountVnd));
        }

        if (requiredRecoveryCoins <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredRecoveryCoins));
        }

        var fee = checked(originalAmountVnd * FeePercent / 100);
        return new RefundRequest
        {
            Id = id,
            PaymentOrderId = paymentOrderId,
            UserId = userId,
            PaymentOrderType = PaymentOrderTypes.TopUp,
            Reason = reason,
            OriginalAmountVnd = originalAmountVnd,
            FeeAmountVnd = fee,
            NetRefundVnd = checked(originalAmountVnd - fee),
            RequiredRecoveryCoins = requiredRecoveryCoins,
            Status = RefundRequestStatuses.Requested,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
            RequestFingerprintVersion = requestFingerprintVersion,
            RequestedAt = requestedAt,
            Version = 1,
        };
    }

    public void BeginReview(Guid actorId, DateTimeOffset reviewedAt)
    {
        EnsureIdentifier(actorId, nameof(actorId));
        EnsureUtc(reviewedAt, nameof(reviewedAt));
        if (Status != RefundRequestStatuses.Requested)
        {
            throw new InvalidOperationException("Only a requested refund can enter review.");
        }

        Status = RefundRequestStatuses.UnderReview;
        ReviewedBy = actorId;
        ReviewedAt = reviewedAt;
        Version = checked(Version + 1);
    }

    public void ReserveRecovery(
        Guid actorId,
        string decisionNote,
        long expectedVersion,
        DateTimeOffset approvedAt)
    {
        EnsureIdentifier(actorId, nameof(actorId));
        EnsureText(decisionNote, MaximumReasonLength, nameof(decisionNote));
        EnsureUtc(approvedAt, nameof(approvedAt));
        EnsureVersion(expectedVersion);
        if (Version != expectedVersion)
        {
            throw new RefundRequestConcurrencyException();
        }

        if (Status != RefundRequestStatuses.UnderReview ||
            ReviewedBy is null || ReviewedAt is null)
        {
            throw new InvalidOperationException(
                "Only a reviewed refund can reserve its original Paid Coin lot.");
        }

        Status = RefundRequestStatuses.RecoveryReserved;
        ApprovedBy = actorId;
        ApprovedAt = approvedAt;
        DecisionReason = decisionNote;
        Version = checked(Version + 1);
    }

    public void Reject(
        Guid actorId,
        string reason,
        long expectedVersion,
        DateTimeOffset rejectedAt)
    {
        EnsureIdentifier(actorId, nameof(actorId));
        EnsureText(reason, MaximumReasonLength, nameof(reason));
        EnsureUtc(rejectedAt, nameof(rejectedAt));
        EnsureVersion(expectedVersion);
        if (Version != expectedVersion)
        {
            throw new RefundRequestConcurrencyException();
        }

        if (Status != RefundRequestStatuses.UnderReview ||
            ReviewedBy is null || ReviewedAt is null)
        {
            throw new InvalidOperationException("Only a reviewed refund can be rejected.");
        }

        Status = RefundRequestStatuses.Rejected;
        DecisionReason = reason;
        Version = checked(Version + 1);
    }

    public void RecordTransferAttempt(string reportedOutcome, DateTimeOffset recordedAt)
    {
        EnsureUtc(recordedAt, nameof(recordedAt));
        if (Status != RefundRequestStatuses.RecoveryReserved)
        {
            throw new InvalidOperationException(
                "Only a reserved refund can record its transfer attempt.");
        }

        Status = reportedOutcome == RefundTransferReportedOutcomes.Unknown
            ? RefundRequestStatuses.NeedsReconciliation
            : RefundRequestStatuses.TransferPending;
        Version = checked(Version + 1);
    }

    public void Complete(
        Guid actorId,
        long expectedVersion,
        DateTimeOffset completedAt)
    {
        EnsureIdentifier(actorId, nameof(actorId));
        EnsureUtc(completedAt, nameof(completedAt));
        EnsureVersion(expectedVersion);
        if (Version != expectedVersion)
        {
            throw new RefundRequestConcurrencyException();
        }

        if (Status is not (RefundRequestStatuses.TransferPending or
                RefundRequestStatuses.NeedsReconciliation))
        {
            throw new InvalidOperationException(
                "Only a pending transfer can complete a refund.");
        }

        Status = RefundRequestStatuses.Completed;
        CompletedBy = actorId;
        CompletedAt = completedAt;
        Version = checked(Version + 1);
    }

    public void ConfirmTransferFailed(
        Guid actorId,
        long expectedVersion,
        DateTimeOffset confirmedAt)
    {
        EnsureIdentifier(actorId, nameof(actorId));
        EnsureUtc(confirmedAt, nameof(confirmedAt));
        EnsureVersion(expectedVersion);
        if (Version != expectedVersion)
        {
            throw new RefundRequestConcurrencyException();
        }

        if (Status is not (RefundRequestStatuses.TransferPending or
                RefundRequestStatuses.NeedsReconciliation))
        {
            throw new InvalidOperationException(
                "Only a pending transfer can be confirmed failed.");
        }

        Status = RefundRequestStatuses.TransferConfirmedFailed;
        CompletedBy = actorId;
        CompletedAt = confirmedAt;
        Version = checked(Version + 1);
    }

    public void MarkReservationReleased(DateTimeOffset releasedAt)
    {
        EnsureUtc(releasedAt, nameof(releasedAt));
        if (Status != RefundRequestStatuses.TransferConfirmedFailed)
        {
            throw new InvalidOperationException(
                "Only a confirmed failed transfer can release its reservation.");
        }

        Status = RefundRequestStatuses.ReservationReleased;
        Version = checked(Version + 1);
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", parameterName);
        }
    }

    private static void EnsureText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value != value.Trim() || value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void EnsureFingerprint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != FingerprintLength ||
            value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Request fingerprint must be a lowercase SHA-256 hex digest.",
                nameof(value));
        }
    }

    private static void EnsureVersion(long value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static void EnsureSecurityVersion(string value)
    {
        EnsureText(value, MaximumSecurityVersionLength, nameof(value));
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                                   character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Security version must contain only ASCII letters, digits, '-' or '_'.",
                nameof(value));
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

public sealed class RefundRequestConcurrencyException : Exception
{
    public RefundRequestConcurrencyException()
        : base("The refund request version is stale.")
    {
    }
}
