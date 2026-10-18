using System.Text.Json;
using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;

namespace ComicManagement.Persistence;

internal sealed class EfOutboxWriter(AppDbContext dbContext, IClock clock) : IOutboxWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Guid Enqueue<TPayload>(
        string eventType,
        TPayload payload,
        DateTimeOffset occurredAt)
        where TPayload : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        if (eventType.Length > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventType),
                "Event type cannot exceed 200 characters.");
        }

        var canonicalOccurredAt = PostgreSqlTimestamp.Normalize(occurredAt);
        var availableAt = PostgreSqlTimestamp.Normalize(clock.UtcNow);

        var message = OutboxMessage.Create(
            Guid.NewGuid(),
            eventType,
            JsonSerializer.Serialize(payload, SerializerOptions),
            canonicalOccurredAt,
            availableAt);

        dbContext.OutboxMessages.Add(message);
        return message.Id;
    }

}
