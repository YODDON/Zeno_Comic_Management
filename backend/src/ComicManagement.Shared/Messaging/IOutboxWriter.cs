namespace ComicManagement.Shared.Messaging;

public interface IOutboxWriter
{
    Guid Enqueue<TPayload>(
        string eventType,
        TPayload payload,
        DateTimeOffset occurredAt)
        where TPayload : notnull;
}
