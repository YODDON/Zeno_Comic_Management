namespace ComicManagement.Wallet.Accounts;

public sealed class Wallet
{
    private Wallet()
    {
    }

    public Guid UserId { get; private set; }

    public long PaidAvailable { get; private set; }

    public long PaidReserved { get; private set; }

    public long BonusAvailable { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long TotalAvailable => checked(PaidAvailable + BonusAvailable);

    public static Wallet Create(Guid userId, DateTimeOffset createdAt)
    {
        EnsureIdentifier(userId, nameof(userId));
        EnsureUtc(createdAt, nameof(createdAt));

        return new Wallet
        {
            UserId = userId,
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    public void CreditPaid(long amount, DateTimeOffset occurredAt)
    {
        EnsurePositive(amount, nameof(amount));
        var newPaidAvailable = checked(PaidAvailable + amount);
        _ = checked(newPaidAvailable + BonusAvailable);
        Mutate(amount, occurredAt, () => PaidAvailable = newPaidAvailable);
    }

    public void CreditBonus(long amount, DateTimeOffset occurredAt)
    {
        EnsurePositive(amount, nameof(amount));
        var newBonusAvailable = checked(BonusAvailable + amount);
        _ = checked(PaidAvailable + newBonusAvailable);
        Mutate(amount, occurredAt, () => BonusAvailable = newBonusAvailable);
    }

    public void SpendPaid(long amount, DateTimeOffset occurredAt)
    {
        EnsureSufficient(PaidAvailable, amount, "Paid available balance is insufficient.");
        Mutate(amount, occurredAt, () => PaidAvailable -= amount);
    }

    public void SpendBonus(long amount, DateTimeOffset occurredAt)
    {
        EnsureSufficient(BonusAvailable, amount, "Bonus available balance is insufficient.");
        Mutate(amount, occurredAt, () => BonusAvailable -= amount);
    }

    public void ReservePaid(long amount, DateTimeOffset occurredAt)
    {
        EnsureSufficient(PaidAvailable, amount, "Paid available balance is insufficient.");
        var newPaidAvailable = PaidAvailable - amount;
        var newPaidReserved = checked(PaidReserved + amount);
        Mutate(
            amount,
            occurredAt,
            () =>
            {
                PaidAvailable = newPaidAvailable;
                PaidReserved = newPaidReserved;
            });
    }

    public void ConsumeReservedPaid(long amount, DateTimeOffset occurredAt)
    {
        EnsureSufficient(PaidReserved, amount, "Paid reserved balance is insufficient.");
        Mutate(amount, occurredAt, () => PaidReserved -= amount);
    }

    public void ReleaseReservedPaid(long amount, DateTimeOffset occurredAt)
    {
        EnsureSufficient(PaidReserved, amount, "Paid reserved balance is insufficient.");
        var newPaidReserved = PaidReserved - amount;
        var newPaidAvailable = checked(PaidAvailable + amount);
        _ = checked(newPaidAvailable + BonusAvailable);
        Mutate(
            amount,
            occurredAt,
            () =>
            {
                PaidReserved = newPaidReserved;
                PaidAvailable = newPaidAvailable;
            });
    }

    private void Mutate(long amount, DateTimeOffset occurredAt, Action mutation)
    {
        EnsurePositive(amount, nameof(amount));
        EnsureUtc(occurredAt, nameof(occurredAt));
        if (occurredAt < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredAt),
                "Wallet mutations cannot move the aggregate timestamp backwards.");
        }

        mutation();
        UpdatedAt = occurredAt;
        Version = checked(Version + 1);
    }

    private static void EnsureSufficient(long available, long amount, string message)
    {
        EnsurePositive(amount, nameof(amount));
        if (available < amount)
        {
            throw new InvalidOperationException(message);
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
