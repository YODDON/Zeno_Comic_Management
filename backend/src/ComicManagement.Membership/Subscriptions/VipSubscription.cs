namespace ComicManagement.Membership.Subscriptions;

public sealed record VipExtension(
    DateTimeOffset? PreviousExpiry,
    DateTimeOffset EffectiveStart,
    DateTimeOffset NewExpiry);

public sealed class VipSubscription
{
    private VipSubscription()
    {
    }

    public Guid UserId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset LastFulfilledAt { get; private set; }

    public long Version { get; private set; }

    public static (VipSubscription Subscription, VipExtension Extension) Create(
        Guid userId,
        DateTimeOffset fulfilledAt,
        int durationHours)
    {
        EnsureInputs(userId, fulfilledAt, durationHours);
        var expiresAt = fulfilledAt.AddHours(durationHours);
        return (
            new VipSubscription
            {
                UserId = userId,
                ExpiresAt = expiresAt,
                LastFulfilledAt = fulfilledAt,
                Version = 1,
            },
            new VipExtension(null, fulfilledAt, expiresAt));
    }

    public VipExtension Extend(DateTimeOffset fulfilledAt, int durationHours)
    {
        EnsureInputs(UserId, fulfilledAt, durationHours);
        var previousExpiry = ExpiresAt;
        var effectiveStart = previousExpiry > fulfilledAt
            ? previousExpiry
            : fulfilledAt;
        var newExpiry = effectiveStart.AddHours(durationHours);
        ExpiresAt = newExpiry;
        LastFulfilledAt = fulfilledAt;
        Version = checked(Version + 1);
        return new VipExtension(previousExpiry, effectiveStart, newExpiry);
    }

    private static void EnsureInputs(
        Guid userId,
        DateTimeOffset fulfilledAt,
        int durationHours)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (fulfilledAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(fulfilledAt));
        }

        if (durationHours <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationHours));
        }
    }
}
