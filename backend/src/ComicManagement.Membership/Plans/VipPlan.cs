namespace ComicManagement.Membership.Plans;

public sealed class VipPlan
{
    public static readonly Guid InitialPlanId =
        Guid.Parse("8c884d82-fdc7-4e94-893d-6caa95a22f7a");

    public const string InitialCode = "VIP_30_DAY";
    public const string InitialName = "VIP 30 Days";
    public const long InitialPriceVnd = 29_000;
    public const int FixedDurationHours = 720;
    public const int FixedDiscountPercent = 20;
    public const int MaximumCodeLength = 64;
    public const int MaximumNameLength = 120;

    private VipPlan()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public long PriceVnd { get; private set; }

    public int DurationHours { get; private set; }

    public int DiscountPercent { get; private set; }

    public bool IsActive { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static VipPlan CreateInitial(DateTimeOffset updatedAt)
    {
        EnsureUtc(updatedAt, nameof(updatedAt));
        return new VipPlan
        {
            Id = InitialPlanId,
            Code = InitialCode,
            Name = InitialName,
            PriceVnd = InitialPriceVnd,
            DurationHours = FixedDurationHours,
            DiscountPercent = FixedDiscountPercent,
            IsActive = true,
            Version = 1,
            UpdatedAt = updatedAt,
        };
    }

    public void ChangeFutureOrderPrice(
        long priceVnd,
        long expectedVersion,
        DateTimeOffset updatedAt)
    {
        if (priceVnd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priceVnd));
        }

        EnsureUtc(updatedAt, nameof(updatedAt));
        if (expectedVersion != Version)
        {
            throw new VipPlanConcurrencyException();
        }

        PriceVnd = priceVnd;
        UpdatedAt = updatedAt;
        Version = checked(Version + 1);
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }
}

public sealed class VipPlanConcurrencyException : Exception
{
    public VipPlanConcurrencyException()
        : base("The VIP plan version is stale.")
    {
    }
}
