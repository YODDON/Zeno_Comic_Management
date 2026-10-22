namespace ComicManagement.Wallet.Reservations;

public static class CoinReservationStatuses
{
    public const string Reserved = "Reserved";
    public const string Consumed = "Consumed";
    public const string Released = "Released";
}

public static class RefundApprovalResultStatuses
{
    public const string RecoveryReserved = "RecoveryReserved";
}

public sealed class CoinReservation
{
    public const int MaximumStatusLength = 16;
    public const int MaximumIdempotencyKeyLength = 200;
    public const int FingerprintLength = 64;
    public const int MaximumApprovalResultStatusLength = 32;
    public const int MaximumDecisionNoteLength = 1000;

    private CoinReservation()
    {
    }

    public Guid Id { get; private set; }

    public Guid RefundRequestId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid PaidCoinLotId { get; private set; }

    public Guid WalletOperationId { get; private set; }

    public Guid ApprovedBy { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string RequestFingerprint { get; private set; } = string.Empty;

    public long AmountCoins { get; private set; }

    public string ApprovalResultStatus { get; private set; } = string.Empty;

    public long ApprovalResultVersion { get; private set; }

    public Guid ApprovalResultReviewedBy { get; private set; }

    public DateTimeOffset ApprovalResultReviewedAt { get; private set; }

    public string ApprovalResultDecisionNote { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public static CoinReservation Create(
        Guid id,
        Guid refundRequestId,
        Guid userId,
        Guid paidCoinLotId,
        Guid walletOperationId,
        Guid approvedBy,
        string idempotencyKey,
        string requestFingerprint,
        long amountCoins,
        long approvalResultVersion,
        Guid approvalResultReviewedBy,
        DateTimeOffset approvalResultReviewedAt,
        string approvalResultDecisionNote,
        DateTimeOffset createdAt)
    {
        EnsureIdentifier(id, nameof(id));
        EnsureIdentifier(refundRequestId, nameof(refundRequestId));
        EnsureIdentifier(userId, nameof(userId));
        EnsureIdentifier(paidCoinLotId, nameof(paidCoinLotId));
        EnsureIdentifier(walletOperationId, nameof(walletOperationId));
        EnsureIdentifier(approvedBy, nameof(approvedBy));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey != idempotencyKey.Trim() ||
            idempotencyKey.Length > MaximumIdempotencyKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        }

        ArgumentNullException.ThrowIfNull(requestFingerprint);
        if (requestFingerprint.Length != FingerprintLength ||
            requestFingerprint.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Fingerprint must be lowercase SHA-256 hex.",
                nameof(requestFingerprint));
        }
        if (amountCoins <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountCoins));
        }

        if (approvalResultVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(approvalResultVersion));
        }

        EnsureIdentifier(approvalResultReviewedBy, nameof(approvalResultReviewedBy));
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalResultDecisionNote);
        if (approvalResultDecisionNote != approvalResultDecisionNote.Trim() ||
            approvalResultDecisionNote.Length > MaximumDecisionNoteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(approvalResultDecisionNote));
        }

        if (createdAt.Offset != TimeSpan.Zero || approvalResultReviewedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamps must use the UTC offset.");
        }

        return new CoinReservation
        {
            Id = id,
            RefundRequestId = refundRequestId,
            UserId = userId,
            PaidCoinLotId = paidCoinLotId,
            WalletOperationId = walletOperationId,
            ApprovedBy = approvedBy,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
            AmountCoins = amountCoins,
            ApprovalResultStatus = RefundApprovalResultStatuses.RecoveryReserved,
            ApprovalResultVersion = approvalResultVersion,
            ApprovalResultReviewedBy = approvalResultReviewedBy,
            ApprovalResultReviewedAt = approvalResultReviewedAt,
            ApprovalResultDecisionNote = approvalResultDecisionNote,
            Status = CoinReservationStatuses.Reserved,
            CreatedAt = createdAt,
        };
    }

    public void Consume(DateTimeOffset resolvedAt)
    {
        Resolve(CoinReservationStatuses.Consumed, resolvedAt);
    }

    public void Release(DateTimeOffset resolvedAt)
    {
        Resolve(CoinReservationStatuses.Released, resolvedAt);
    }

    private void Resolve(string status, DateTimeOffset resolvedAt)
    {
        if (resolvedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(resolvedAt));
        if (Status != CoinReservationStatuses.Reserved)
            throw new InvalidOperationException("Only a reserved coin reservation can be resolved.");
        if (resolvedAt < CreatedAt)
            throw new ArgumentOutOfRangeException(nameof(resolvedAt));

        Status = status;
        ResolvedAt = resolvedAt;
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", parameterName);
        }
    }
}
