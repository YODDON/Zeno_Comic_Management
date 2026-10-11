using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicManagement.Persistence;

internal sealed class OutboxBatchProcessor(
    AppDbContext dbContext,
    OutboxMessageDispatcher dispatcher,
    IClock clock,
    IOptions<OutboxOptions> options,
    ILogger<OutboxBatchProcessor> logger)
{
    internal async Task<OutboxBatchResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var claimedMessages = await ClaimBatchAsync(cancellationToken);
        var completedCount = 0;
        var failedCount = 0;

        foreach (var claimedMessage in claimedMessages)
        {
            var completed = await ProcessMessageAsync(claimedMessage, cancellationToken);

            if (completed)
            {
                completedCount++;
            }
            else
            {
                failedCount++;
            }
        }

        return new OutboxBatchResult(claimedMessages.Count, completedCount, failedCount);
    }

    private async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimBatchAsync(
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var lockedUntil = now.Add(options.Value.LockDuration);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var messages = await dbContext.OutboxMessages
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM infrastructure.outbox_messages
                WHERE available_at <= {now}
                  AND (
                    status = {OutboxMessageStatuses.Pending}
                    OR (status = {OutboxMessageStatuses.Processing} AND locked_until <= {now})
                  )
                ORDER BY available_at, occurred_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT {options.Value.BatchSize}
                """)
            .ToArrayAsync(cancellationToken);

        foreach (var message in messages)
        {
            message.Claim(lockedUntil);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var claimedMessages = messages
            .Select(message => new ClaimedOutboxMessage(
                message.Id,
                message.AttemptCount,
                lockedUntil))
            .ToArray();

        dbContext.ChangeTracker.Clear();
        return claimedMessages;
    }

    private async Task<bool> ProcessMessageAsync(
        ClaimedOutboxMessage claimedMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var message = await dbContext.OutboxMessages.SingleOrDefaultAsync(
                candidate => candidate.Id == claimedMessage.Id &&
                    candidate.Status == OutboxMessageStatuses.Processing &&
                    candidate.LockedUntil == claimedMessage.LockedUntil,
                cancellationToken);

            if (message is null)
            {
                return false;
            }

            await dispatcher.DispatchAsync(message, cancellationToken);
            message.Complete();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (Exception exception)
        {
            dbContext.ChangeTracker.Clear();
            await RecordFailureAsync(claimedMessage, exception, cancellationToken);
            return false;
        }
    }

    private async Task RecordFailureAsync(
        ClaimedOutboxMessage claimedMessage,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var exhausted = claimedMessage.AttemptCount >= options.Value.MaxAttempts;
        var status = exhausted
            ? OutboxMessageStatuses.Failed
            : OutboxMessageStatuses.Pending;
        var availableAt = exhausted
            ? now
            : now.Add(CalculateRetryDelay(claimedMessage.AttemptCount));
        var errorType = exception.GetType().Name;

        var updated = await dbContext.OutboxMessages
            .Where(message => message.Id == claimedMessage.Id &&
                message.Status == OutboxMessageStatuses.Processing &&
                message.LockedUntil == claimedMessage.LockedUntil &&
                message.AttemptCount == claimedMessage.AttemptCount)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, status)
                    .SetProperty(message => message.AvailableAt, availableAt)
                    .SetProperty(message => message.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(message => message.LastErrorRedacted, errorType),
                cancellationToken);

        if (updated == 0)
        {
            return;
        }

        if (exhausted)
        {
            logger.LogError(
                "Outbox message {MessageId} exhausted {AttemptCount} attempts with {ErrorType}.",
                claimedMessage.Id,
                claimedMessage.AttemptCount,
                errorType);
        }
        else
        {
            logger.LogWarning(
                "Outbox message {MessageId} attempt {AttemptCount} failed with {ErrorType}; retry scheduled.",
                claimedMessage.Id,
                claimedMessage.AttemptCount,
                errorType);
        }
    }

    private TimeSpan CalculateRetryDelay(int attemptCount)
    {
        var delay = options.Value.BaseRetryDelay;

        for (var attempt = 1; attempt < attemptCount; attempt++)
        {
            if (delay >= options.Value.MaxRetryDelay / 2)
            {
                return options.Value.MaxRetryDelay;
            }

            delay += delay;
        }

        return delay <= options.Value.MaxRetryDelay
            ? delay
            : options.Value.MaxRetryDelay;
    }

    private sealed record ClaimedOutboxMessage(
        Guid Id,
        int AttemptCount,
        DateTimeOffset LockedUntil);
}

internal readonly record struct OutboxBatchResult(
    int ClaimedCount,
    int CompletedCount,
    int FailedCount);
