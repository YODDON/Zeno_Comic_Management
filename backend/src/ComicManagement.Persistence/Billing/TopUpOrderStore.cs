using System.Data;
using ComicManagement.Billing.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ComicManagement.Persistence;

internal sealed class TopUpOrderStore(AppDbContext dbContext) : ITopUpOrderStore
{
    public async Task<TopUpOrderStoreResult> CreateOrReplayAsync(
        PaymentOrder candidate,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireIdempotencyLockAsync(candidate, cancellationToken);

        var existing = await dbContext.PaymentOrders.SingleOrDefaultAsync(
            order => order.UserId == candidate.UserId &&
                     order.OrderType == candidate.OrderType &&
                     order.IdempotencyKey == candidate.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing.RequestFingerprint == candidate.RequestFingerprint
                ? new TopUpOrderStoreResult(TopUpOrderOutcome.Succeeded, existing)
                : new TopUpOrderStoreResult(TopUpOrderOutcome.IdempotencyConflict);
        }

        dbContext.PaymentOrders.Add(candidate);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TopUpOrderStoreResult(TopUpOrderOutcome.Succeeded, candidate);
    }

    private async Task AcquireIdempotencyLockAsync(
        PaymentOrder candidate,
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
            candidate.UserId.ToString("N"),
            ":",
            candidate.OrderType,
            ":",
            candidate.IdempotencyKey);
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
