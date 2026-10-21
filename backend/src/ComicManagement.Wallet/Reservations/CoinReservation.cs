namespace ComicManagement.Wallet.Reservations;

public static class CoinReservationStatuses
{
    public const string Reserved = "Reserved";
    public const string Consumed = "Consumed";
    public const string Released = "Released";
}

public sealed class CoinReservation
{
    public const int MaximumStatusLength = 16;
    public const int MaximumIdempotencyKeyLength = 200;
    public const int FingerprintLength = 64;

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

        if (createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(createdAt));
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
            Status = CoinReservationStatuses.Reserved,
            CreatedAt = createdAt,
        };
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", parameterName);
        }
    }
}
