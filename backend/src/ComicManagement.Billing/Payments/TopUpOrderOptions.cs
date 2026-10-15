namespace ComicManagement.Billing.Payments;

public sealed class TopUpOrderOptions
{
    public const string SectionName = "Billing";

    public int TopUpOrderTtlMinutes { get; set; } = 30;
}

public sealed class TopUpOrderPolicy
{
    public TopUpOrderPolicy(TimeSpan timeToLive)
    {
        if (timeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive),
                "TopUp order TTL must be positive.");
        }

        TimeToLive = timeToLive;
    }

    public TimeSpan TimeToLive { get; }
}
