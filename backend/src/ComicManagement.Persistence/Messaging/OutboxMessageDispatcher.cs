using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class OutboxMessageDispatcher(
    IEnumerable<IOutboxMessageHandler> handlers,
    AppDbContext dbContext,
    IClock clock)
{
    internal async Task DispatchAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        var matchingHandlers = handlers
            .Where(handler => string.Equals(
                handler.EventType,
                message.EventType,
                StringComparison.Ordinal))
            .OrderBy(handler => handler.ConsumerName, StringComparer.Ordinal)
            .ToArray();

        if (matchingHandlers.Length == 0)
        {
            throw new UnhandledOutboxMessageException();
        }

        if (matchingHandlers.Any(handler => string.IsNullOrWhiteSpace(handler.ConsumerName)) ||
            matchingHandlers.GroupBy(handler => handler.ConsumerName, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
        {
            throw new InvalidOutboxHandlerRegistrationException();
        }

        var envelope = new OutboxMessageEnvelope(
            message.Id,
            message.EventType,
            message.Payload,
            message.OccurredAt,
            message.AttemptCount);

        foreach (var handler in matchingHandlers)
        {
            var wasProcessed = await dbContext.ProcessedMessages.AnyAsync(
                processed => processed.Consumer == handler.ConsumerName &&
                    processed.MessageId == message.Id,
                cancellationToken);

            if (wasProcessed)
            {
                continue;
            }

            await handler.HandleAsync(envelope, cancellationToken);
            dbContext.ProcessedMessages.Add(ProcessedMessage.Create(
                handler.ConsumerName,
                message.Id,
                clock.UtcNow));
        }
    }

    private sealed class InvalidOutboxHandlerRegistrationException : InvalidOperationException;
}

internal sealed class UnhandledOutboxMessageException : InvalidOperationException;
