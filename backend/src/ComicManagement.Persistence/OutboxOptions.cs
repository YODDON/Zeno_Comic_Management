namespace ComicManagement.Persistence;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; set; } = 20;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxAttempts { get; set; } = 10;
}
