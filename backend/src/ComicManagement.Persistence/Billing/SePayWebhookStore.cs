using System.Data;
using ComicManagement.Billing.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ComicManagement.Persistence;

internal interface ISePayWebhookCriticalSectionObserver
{
    Task AfterLockAcquiredAsync(
        string providerTransactionId,
        CancellationToken cancellationToken);
}

internal sealed class NoOpSePayWebhookCriticalSectionObserver :
    ISePayWebhookCriticalSectionObserver
{
    public Task AfterLockAcquiredAsync(
        string providerTransactionId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SePayWebhookStore(
    AppDbContext dbContext,
    ISePayWebhookCriticalSectionObserver criticalSectionObserver) : ISePayWebhookStore
{
    public async Task<SePayWebhookStoreResult> IngestAsync(
        SePayNormalizedTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await using var databaseTransaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        await AcquireProviderTransactionLockAsync(transaction, cancellationToken);
        await criticalSectionObserver.AfterLockAcquiredAsync(
            transaction.ProviderTransactionId,
            cancellationToken);
        var serverNow = await GetAuthoritativeServerTimeAsync(cancellationToken);

        var existing = await dbContext.ProviderTransactions.SingleOrDefaultAsync(
            candidate => candidate.Provider == PaymentProviders.SePay &&
                         candidate.ProviderTransactionId == transaction.ProviderTransactionId,
            cancellationToken);

        ProviderTransaction authoritative;
        SePayWebhookStoreOutcome outcome;
        string deliveryResult;
        string? errorCode;
        if (existing is null)
        {
            authoritative = ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                transaction,
                serverNow);
            dbContext.ProviderTransactions.Add(authoritative);
            if (transaction.ReconciliationReason is null)
            {
                outcome = SePayWebhookStoreOutcome.Accepted;
                deliveryResult = WebhookDeliveryProcessingResults.Accepted;
                errorCode = null;
            }
            else
            {
                outcome = SePayWebhookStoreOutcome.ReconciliationRequired;
                deliveryResult = WebhookDeliveryProcessingResults.ReconciliationRequired;
                errorCode = transaction.ReconciliationReason;
            }
        }
        else if (string.Equals(
                     existing.PayloadFingerprint,
                     transaction.PayloadFingerprint,
                     StringComparison.Ordinal))
        {
            authoritative = existing;
            outcome = SePayWebhookStoreOutcome.Duplicate;
            deliveryResult = WebhookDeliveryProcessingResults.Duplicate;
            errorCode = null;
        }
        else
        {
            authoritative = existing;
            existing.RequireReconciliation(
                SePayReconciliationReasons.ProviderTransactionConflict);
            outcome = SePayWebhookStoreOutcome.ReconciliationRequired;
            deliveryResult = WebhookDeliveryProcessingResults.ReconciliationRequired;
            errorCode = SePayReconciliationReasons.ProviderTransactionConflict;
        }

        dbContext.WebhookDeliveries.Add(WebhookDelivery.CreateAuthenticated(
            Guid.NewGuid(),
            authoritative.Id,
            serverNow,
            transaction.PayloadFingerprint,
            deliveryResult,
            errorCode));
        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);
        return new SePayWebhookStoreResult(outcome, authoritative.Id);
    }

    private async Task AcquireProviderTransactionLockAsync(
        SePayNormalizedTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException(
                "A provider transaction lock requires a database transaction.");
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = string.Concat(
            PaymentProviders.SePay,
            ":",
            transaction.ProviderTransactionId);
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<DateTimeOffset> GetAuthoritativeServerTimeAsync(
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException(
                "Server time requires a database transaction.");
        command.CommandText = "SELECT clock_timestamp();";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(timestamp.ToUniversalTime()),
            _ => throw new InvalidOperationException(
                "PostgreSQL did not return server time."),
        };
    }
}
