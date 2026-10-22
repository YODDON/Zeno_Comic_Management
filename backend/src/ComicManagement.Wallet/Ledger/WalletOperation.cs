namespace ComicManagement.Wallet.Ledger;

public static class WalletOperationTypes
{
    public const string TopUpCredit = "TopUpCredit";
    public const string ChapterUnlock = "ChapterUnlock";
    public const string MissionReward = "MissionReward";
    public const string RefundReserve = "RefundReserve";
}

public static class WalletOperationSourceTypes
{
    public const string PaymentOrder = "PaymentOrder";
    public const string ReadingUnlock = "ReadingUnlock";
    public const string RewardGrant = "RewardGrant";
    public const string RefundRequest = "RefundRequest";
}

public sealed class WalletOperation
{
    public const int MaximumOperationTypeLength = 64;
    public const int MaximumSourceTypeLength = 64;
    public const int MaximumIdempotencyKeyLength = 200;

    private WalletOperation()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string OperationType { get; private set; } = string.Empty;

    public string SourceType { get; private set; } = string.Empty;

    public Guid SourceId { get; private set; }

    public string? IdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static WalletOperation Create(
        Guid id,
        Guid userId,
        string operationType,
        string sourceType,
        Guid sourceId,
        string? idempotencyKey,
        DateTimeOffset createdAt)
    {
        EnsureIdentifier(id, nameof(id));
        EnsureIdentifier(userId, nameof(userId));
        EnsureText(operationType, MaximumOperationTypeLength, nameof(operationType));
        EnsureText(sourceType, MaximumSourceTypeLength, nameof(sourceType));
        EnsureIdentifier(sourceId, nameof(sourceId));
        if (idempotencyKey is not null)
        {
            EnsureText(idempotencyKey, MaximumIdempotencyKeyLength, nameof(idempotencyKey));
        }

        EnsureUtc(createdAt, nameof(createdAt));
        return new WalletOperation
        {
            Id = id,
            UserId = userId,
            OperationType = operationType,
            SourceType = sourceType,
            SourceId = sourceId,
            IdempotencyKey = idempotencyKey,
            CreatedAt = createdAt,
        };
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
