namespace ComicManagement.Wallet.Ledger;

public static class WalletCoinTypes
{
    public const string Paid = "Paid";
    public const string Bonus = "Bonus";
}

public static class WalletBuckets
{
    public const string Available = "Available";
    public const string Reserved = "Reserved";
}

public sealed class WalletEntry
{
    private WalletEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid OperationId { get; private set; }

    public Guid UserId { get; private set; }

    public string CoinType { get; private set; } = string.Empty;

    public string Bucket { get; private set; } = string.Empty;

    public long DeltaCoins { get; private set; }

    public int LegOrder { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static WalletEntry Create(
        Guid id,
        WalletOperation operation,
        string coinType,
        string bucket,
        long deltaCoins,
        int legOrder,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Wallet entry ID is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(operation);
        if (coinType is not (WalletCoinTypes.Paid or WalletCoinTypes.Bonus))
        {
            throw new ArgumentException("Wallet coin type is invalid.", nameof(coinType));
        }

        if (bucket is not (WalletBuckets.Available or WalletBuckets.Reserved))
        {
            throw new ArgumentException("Wallet bucket is invalid.", nameof(bucket));
        }

        if (coinType == WalletCoinTypes.Bonus && bucket == WalletBuckets.Reserved)
        {
            throw new ArgumentException("Bonus Coins cannot be reserved.", nameof(bucket));
        }

        if (deltaCoins == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaCoins));
        }

        if (legOrder < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(legOrder));
        }

        if (createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(createdAt));
        }

        return new WalletEntry
        {
            Id = id,
            OperationId = operation.Id,
            UserId = operation.UserId,
            CoinType = coinType,
            Bucket = bucket,
            DeltaCoins = deltaCoins,
            LegOrder = legOrder,
            CreatedAt = createdAt,
        };
    }
}
