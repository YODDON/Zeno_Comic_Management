namespace ComicManagement.Wallet.Lots;

public sealed class PaidCoinLot
{
    private PaidCoinLot()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid TopUpOrderId { get; private set; }

    public long GrantedCoins { get; private set; }

    public long SpentCoins { get; private set; }

    public long RefundedCoins { get; private set; }

    public long ReservedCoins { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public long Version { get; private set; }

    public long AvailableCoins => checked(
        GrantedCoins - SpentCoins - RefundedCoins - ReservedCoins);

    public static PaidCoinLot Create(
        Guid id,
        Guid userId,
        Guid topUpOrderId,
        long grantedCoins,
        DateTimeOffset createdAt)
    {
        EnsureIdentifier(id, nameof(id));
        EnsureIdentifier(userId, nameof(userId));
        EnsureIdentifier(topUpOrderId, nameof(topUpOrderId));
        EnsurePositive(grantedCoins, nameof(grantedCoins));
        EnsureUtc(createdAt, nameof(createdAt));

        return new PaidCoinLot
        {
            Id = id,
            UserId = userId,
            TopUpOrderId = topUpOrderId,
            GrantedCoins = grantedCoins,
            CreatedAt = createdAt,
            Version = 1,
        };
    }

    public void Spend(long amount)
    {
        EnsureAvailable(amount);
        SpentCoins = checked(SpentCoins + amount);
        Version = checked(Version + 1);
    }

    public void Reserve(long amount)
    {
        EnsureAvailable(amount);
        ReservedCoins = checked(ReservedCoins + amount);
        Version = checked(Version + 1);
    }

    public void RefundReserved(long amount)
    {
        EnsurePositive(amount, nameof(amount));
        if (ReservedCoins < amount)
        {
            throw new InvalidOperationException("Paid lot reserved balance is insufficient.");
        }

        ReservedCoins -= amount;
        RefundedCoins = checked(RefundedCoins + amount);
        Version = checked(Version + 1);
    }

    public void ReleaseReserved(long amount)
    {
        EnsurePositive(amount, nameof(amount));
        if (ReservedCoins < amount)
        {
            throw new InvalidOperationException("Paid lot reserved balance is insufficient.");
        }

        ReservedCoins -= amount;
        Version = checked(Version + 1);
    }

    private void EnsureAvailable(long amount)
    {
        EnsurePositive(amount, nameof(amount));
        if (AvailableCoins < amount)
        {
            throw new InvalidOperationException("Paid lot available balance is insufficient.");
        }
    }

    private static void EnsurePositive(long amount, string parameterName)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Coin amount must be positive.");
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
