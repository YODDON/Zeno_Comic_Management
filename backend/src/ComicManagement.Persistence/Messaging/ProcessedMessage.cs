namespace ComicManagement.Persistence;

internal sealed class ProcessedMessage
{
    private ProcessedMessage()
    {
    }

    internal string Consumer { get; private set; } = string.Empty;

    internal Guid MessageId { get; private set; }

    internal DateTimeOffset ProcessedAt { get; private set; }

    internal static ProcessedMessage Create(
        string consumer,
        Guid messageId,
        DateTimeOffset processedAt) => new()
        {
            Consumer = consumer,
            MessageId = messageId,
            ProcessedAt = processedAt,
        };
}
