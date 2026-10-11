namespace ComicManagement.Shared.Messaging;

public interface IOutboxMessageHandler
{
    string ConsumerName { get; }

    string EventType { get; }

    Task HandleAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken);
}
