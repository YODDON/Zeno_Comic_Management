namespace ComicManagement.Persistence;

internal sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    internal Guid Id { get; private set; }

    internal string EventType { get; private set; } = string.Empty;

    internal string Payload { get; private set; } = string.Empty;

    internal DateTimeOffset OccurredAt { get; private set; }

    internal DateTimeOffset AvailableAt { get; private set; }

    internal string Status { get; private set; } = OutboxMessageStatuses.Pending;

    internal int AttemptCount { get; private set; }

    internal DateTimeOffset? LockedUntil { get; private set; }

    internal string? LastErrorRedacted { get; private set; }

    internal static OutboxMessage Create(
        Guid id,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        DateTimeOffset availableAt) => new()
        {
            Id = id,
            EventType = eventType,
            Payload = payload,
            OccurredAt = occurredAt,
            AvailableAt = availableAt,
        };

    internal void Claim(DateTimeOffset lockedUntil)
    {
        Status = OutboxMessageStatuses.Processing;
        AttemptCount = checked(AttemptCount + 1);
        LockedUntil = lockedUntil;
    }

    internal void Complete()
    {
        Status = OutboxMessageStatuses.Completed;
        LockedUntil = null;
        LastErrorRedacted = null;
    }
}
