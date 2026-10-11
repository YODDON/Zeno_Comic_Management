namespace ComicManagement.Shared.Messaging;

public sealed record OutboxMessageEnvelope(
    Guid Id,
    string EventType,
    string Payload,
    DateTimeOffset OccurredAt,
    int AttemptCount);
