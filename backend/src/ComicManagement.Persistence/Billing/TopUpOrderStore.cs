using System.Data;
using ComicManagement.Billing.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ComicManagement.Persistence;

internal interface ITopUpOrderCriticalSectionObserver
{
    Task AfterLockAcquiredAsync(
        Guid userId,
        string orderType,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class NoOpTopUpOrderCriticalSectionObserver :
    ITopUpOrderCriticalSectionObserver
{
    public Task AfterLockAcquiredAsync(
        Guid userId,
        string orderType,
        string idempotencyKey,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class TopUpOrderStore(
    AppDbContext dbContext,
    ITopUpOrderCriticalSectionObserver criticalSectionObserver) : ITopUpOrderStore
{
    public async Task<TopUpOrderStoreResult> CreateOrReplayAsync(
        CreateTopUpOrderStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireIdempotencyLockAsync(request, cancellationToken);
        await criticalSectionObserver.AfterLockAcquiredAsync(
            request.UserId,
            PaymentOrderTypes.TopUp,
            request.IdempotencyKey,
            cancellationToken);
        var serverNow = await GetAuthoritativeServerTimeAsync(cancellationToken);

        var existing = await dbContext.PaymentOrders.SingleOrDefaultAsync(
            order => order.UserId == request.UserId &&
                     order.OrderType == PaymentOrderTypes.TopUp &&
                     order.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            var expired = existing.ExpireIfDue(serverNow);
            if (expired)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return existing.RequestFingerprint == request.RequestFingerprint
                ? new TopUpOrderStoreResult(TopUpOrderOutcome.Succeeded, existing)
                : new TopUpOrderStoreResult(TopUpOrderOutcome.IdempotencyConflict);
        }

        var candidate = PaymentOrder.CreateTopUp(
            request.OrderId,
            request.UserId,
            $"ZENO-{request.OrderId:N}",
            request.AmountVnd,
            request.PaidCoins,
            request.IdempotencyKey,
            request.RequestFingerprint,
            serverNow,
            serverNow.Add(request.TimeToLive));
        dbContext.PaymentOrders.Add(candidate);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TopUpOrderStoreResult(TopUpOrderOutcome.Succeeded, candidate);
    }

    private async Task AcquireIdempotencyLockAsync(
        CreateTopUpOrderStoreRequest request,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("An idempotency lock requires a transaction.");
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = string.Concat(
            request.UserId.ToString("N"),
            ":",
            PaymentOrderTypes.TopUp,
            ":",
            request.IdempotencyKey);
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<DateTimeOffset> GetAuthoritativeServerTimeAsync(
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Server time requires a transaction.");
        command.CommandText = "SELECT clock_timestamp();";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(timestamp.ToUniversalTime()),
            _ => throw new InvalidOperationException("PostgreSQL did not return server time."),
        };
    }
}
