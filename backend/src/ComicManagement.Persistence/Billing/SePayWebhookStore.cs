using System.Data;
using ComicManagement.Billing.Events;
using ComicManagement.Billing.Payments;
using ComicManagement.Shared.Messaging;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence;

internal interface ISePayWebhookCriticalSectionObserver
{
    Task AfterLockAcquiredAsync(
        string providerTransactionId,
        CancellationToken cancellationToken);
}

internal interface ISePayFulfillmentCriticalSectionObserver
{
    Task AfterFinancialLocksAcquiredAsync(
        string providerTransactionId,
        Guid paymentOrderId,
        CancellationToken cancellationToken);

    Task BeforeFulfillmentCommitAsync(
        string providerTransactionId,
        Guid paymentOrderId,
        CancellationToken cancellationToken);
}

internal sealed class NoOpSePayWebhookCriticalSectionObserver :
    ISePayWebhookCriticalSectionObserver
{
    public Task AfterLockAcquiredAsync(
        string providerTransactionId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class NoOpSePayFulfillmentCriticalSectionObserver :
    ISePayFulfillmentCriticalSectionObserver
{
    public Task AfterFinancialLocksAcquiredAsync(
        string providerTransactionId,
        Guid paymentOrderId,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task BeforeFulfillmentCommitAsync(
        string providerTransactionId,
        Guid paymentOrderId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SePayWebhookStore(
    AppDbContext dbContext,
    IOutboxWriter outboxWriter,
    ISePayWebhookCriticalSectionObserver webhookObserver,
    ISePayFulfillmentCriticalSectionObserver fulfillmentObserver) : ISePayWebhookStore
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
        await webhookObserver.AfterLockAcquiredAsync(
            transaction.ProviderTransactionId,
            cancellationToken);

        var existing = await dbContext.ProviderTransactions.SingleOrDefaultAsync(
            candidate => candidate.Provider == PaymentProviders.SePay &&
                         candidate.ProviderTransactionId == transaction.ProviderTransactionId,
            cancellationToken);
        if (existing is not null &&
            !string.Equals(
                existing.PayloadFingerprint,
                transaction.PayloadFingerprint,
                StringComparison.Ordinal))
        {
            if (existing.ProcessingStatus != ProviderTransactionProcessingStatuses.Fulfilled)
            {
                existing.RequireReconciliation(
                    SePayReconciliationReasons.ProviderTransactionConflict);
            }

            var conflictTime = await GetAuthoritativeServerTimeAsync(cancellationToken);
            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                existing,
                transaction.PayloadFingerprint,
                conflictTime,
                WebhookDeliveryProcessingResults.ReconciliationRequired,
                SePayReconciliationReasons.ProviderTransactionConflict,
                cancellationToken);
            return new SePayWebhookStoreResult(
                SePayWebhookStoreOutcome.ReconciliationRequired,
                existing.Id,
                IsReplay: true);
        }

        var isReplay = existing is not null;
        if (existing is not null &&
            existing.ProcessingStatus != ProviderTransactionProcessingStatuses.Received)
        {
            var replayTime = await GetAuthoritativeServerTimeAsync(cancellationToken);
            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                existing,
                transaction.PayloadFingerprint,
                replayTime,
                WebhookDeliveryProcessingResults.Duplicate,
                errorCode: null,
                cancellationToken);
            return new SePayWebhookStoreResult(
                ToStoreOutcome(existing),
                existing.Id,
                IsReplay: true);
        }

        if (transaction.ReconciliationReason is not null)
        {
            var reconciliationTime = await GetAuthoritativeServerTimeAsync(cancellationToken);
            var providerTransaction = existing ?? ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                transaction,
                reconciliationTime);
            if (existing is null)
            {
                dbContext.ProviderTransactions.Add(providerTransaction);
            }

            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                providerTransaction,
                transaction.PayloadFingerprint,
                reconciliationTime,
                WebhookDeliveryProcessingResults.ReconciliationRequired,
                transaction.ReconciliationReason,
                cancellationToken);
            return new SePayWebhookStoreResult(
                SePayWebhookStoreOutcome.ReconciliationRequired,
                providerTransaction.Id,
                isReplay);
        }

        var orderProjection = await dbContext.PaymentOrders
            .AsNoTracking()
            .Where(order => order.OrderCode == transaction.PaymentCode)
            .Select(order => new
            {
                order.Id,
                order.UserId,
                order.OrderType,
                order.AmountVnd,
                order.OrderStatus,
                order.CreatedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (orderProjection is null ||
            orderProjection.OrderType != PaymentOrderTypes.TopUp ||
            orderProjection.OrderStatus != TopUpOrderStatuses.Pending)
        {
            var serverNow = await GetAuthoritativeServerTimeAsync(cancellationToken);
            var providerTransaction = existing ?? ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                transaction,
                serverNow);
            if (existing is null)
            {
                dbContext.ProviderTransactions.Add(providerTransaction);
            }

            var reason = orderProjection is null
                ? SePayReconciliationReasons.OrderNotFound
                : orderProjection.OrderType != PaymentOrderTypes.TopUp
                    ? SePayReconciliationReasons.UnsupportedOrderType
                    : orderProjection.OrderStatus == TopUpOrderStatuses.Expired
                        ? SePayReconciliationReasons.LatePayment
                        : SePayReconciliationReasons.OrderAlreadyFulfilled;
            providerTransaction.RequireReconciliation(reason, orderProjection?.Id);
            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                providerTransaction,
                transaction.PayloadFingerprint,
                serverNow,
                WebhookDeliveryProcessingResults.ReconciliationRequired,
                reason,
                cancellationToken);
            return new SePayWebhookStoreResult(
                SePayWebhookStoreOutcome.ReconciliationRequired,
                providerTransaction.Id,
                isReplay);
        }

        if (orderProjection.AmountVnd != transaction.AmountVnd)
        {
            var mismatchedOrder = await LockPaymentOrderAsync(orderProjection.Id, cancellationToken)
                ?? throw new InvalidOperationException("Matched TopUp order no longer exists.");
            var mismatchCheckedAt = await GetAuthoritativeServerTimeAsync(cancellationToken);
            var providerTransaction = existing ?? ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                transaction,
                mismatchCheckedAt);
            if (existing is null)
            {
                dbContext.ProviderTransactions.Add(providerTransaction);
            }

            string reason;
            if (mismatchedOrder.OrderStatus == TopUpOrderStatuses.Expired ||
                mismatchCheckedAt >= mismatchedOrder.ExpiresAt)
            {
                mismatchedOrder.ExpireIfDue(mismatchCheckedAt);
                reason = SePayReconciliationReasons.LatePayment;
            }
            else if (mismatchedOrder.OrderStatus != TopUpOrderStatuses.Pending)
            {
                reason = SePayReconciliationReasons.OrderAlreadyFulfilled;
            }
            else
            {
                reason = SePayReconciliationReasons.AmountMismatch;
            }

            providerTransaction.RequireReconciliation(reason, mismatchedOrder.Id);
            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                providerTransaction,
                transaction.PayloadFingerprint,
                mismatchCheckedAt,
                WebhookDeliveryProcessingResults.ReconciliationRequired,
                reason,
                cancellationToken);
            return new SePayWebhookStoreResult(
                SePayWebhookStoreOutcome.ReconciliationRequired,
                providerTransaction.Id,
                isReplay);
        }

        await EnsureWalletExistsAsync(
            orderProjection.UserId,
            orderProjection.CreatedAt,
            cancellationToken);
        var wallet = await LockWalletAsync(orderProjection.UserId, cancellationToken)
            ?? throw new InvalidOperationException("TopUp wallet could not be created.");
        var paidLots = await LockPaidLotsAsync(orderProjection.UserId, cancellationToken);
        var order = await LockPaymentOrderAsync(orderProjection.Id, cancellationToken)
            ?? throw new InvalidOperationException("Matched TopUp order no longer exists.");
        await fulfillmentObserver.AfterFinancialLocksAcquiredAsync(
            transaction.ProviderTransactionId,
            order.Id,
            cancellationToken);
        var authoritativeNow = await GetAuthoritativeServerTimeAsync(cancellationToken);

        var authoritativeProviderTransaction = existing ?? ProviderTransaction.CreateSePay(
            Guid.NewGuid(),
            transaction,
            authoritativeNow);
        if (existing is null)
        {
            dbContext.ProviderTransactions.Add(authoritativeProviderTransaction);
        }

        string? reconciliationReason = null;
        if (order.OrderType != PaymentOrderTypes.TopUp)
        {
            reconciliationReason = SePayReconciliationReasons.UnsupportedOrderType;
        }
        else if (order.OrderStatus == TopUpOrderStatuses.Expired ||
                 authoritativeNow >= order.ExpiresAt)
        {
            order.ExpireIfDue(authoritativeNow);
            reconciliationReason = SePayReconciliationReasons.LatePayment;
        }
        else if (order.OrderStatus != TopUpOrderStatuses.Pending)
        {
            reconciliationReason = SePayReconciliationReasons.OrderAlreadyFulfilled;
        }
        else if (order.AmountVnd != transaction.AmountVnd)
        {
            reconciliationReason = SePayReconciliationReasons.AmountMismatch;
        }
        else
        {
            var existingOperation = await dbContext.WalletOperations
                .AsNoTracking()
                .AnyAsync(candidate =>
                    candidate.OperationType == WalletOperationTypes.TopUpCredit &&
                    candidate.SourceType == WalletOperationSourceTypes.PaymentOrder &&
                    candidate.SourceId == order.Id,
                    cancellationToken);
            if (existingOperation || paidLots.Any(lot => lot.TopUpOrderId == order.Id))
            {
                reconciliationReason = SePayReconciliationReasons.FulfillmentStateConflict;
            }
        }

        if (reconciliationReason is not null)
        {
            authoritativeProviderTransaction.RequireReconciliation(
                reconciliationReason,
                order.Id);
            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                authoritativeProviderTransaction,
                transaction.PayloadFingerprint,
                authoritativeNow,
                WebhookDeliveryProcessingResults.ReconciliationRequired,
                reconciliationReason,
                cancellationToken);
            return new SePayWebhookStoreResult(
                SePayWebhookStoreOutcome.ReconciliationRequired,
                authoritativeProviderTransaction.Id,
                isReplay);
        }

        var paidCoins = order.PaidCoinsSnapshot ?? throw new InvalidOperationException(
            "A TopUp order requires its Paid Coins snapshot.");
        var walletOperation = WalletOperation.Create(
            Guid.NewGuid(),
            order.UserId,
            WalletOperationTypes.TopUpCredit,
            WalletOperationSourceTypes.PaymentOrder,
            order.Id,
            transaction.ProviderTransactionId,
            authoritativeNow);
        wallet.CreditPaid(paidCoins, authoritativeNow);
        var lot = PaidCoinLot.Create(
            Guid.NewGuid(),
            order.UserId,
            order.Id,
            paidCoins,
            authoritativeNow);
        var entry = WalletEntry.Create(
            Guid.NewGuid(),
            walletOperation,
            WalletCoinTypes.Paid,
            WalletBuckets.Available,
            paidCoins,
            legOrder: 1,
            authoritativeNow);
        order.FulfillTopUp(authoritativeNow);
        authoritativeProviderTransaction.MarkFulfilled(order.Id);
        dbContext.WalletOperations.Add(walletOperation);
        dbContext.WalletEntries.Add(entry);
        dbContext.PaidCoinLots.Add(lot);
        outboxWriter.Enqueue(
            BillingEventTypes.WalletTopUpCompleted,
            new WalletTopUpCompletedEvent(
                order.Id,
                authoritativeProviderTransaction.Id,
                order.UserId,
                order.AmountVnd,
                paidCoins,
                authoritativeNow),
            authoritativeNow);
        await fulfillmentObserver.BeforeFulfillmentCommitAsync(
            transaction.ProviderTransactionId,
            order.Id,
            cancellationToken);

        await AddDeliveryAndCommitAsync(
            databaseTransaction,
            authoritativeProviderTransaction,
            transaction.PayloadFingerprint,
            authoritativeNow,
            WebhookDeliveryProcessingResults.Accepted,
            errorCode: null,
            cancellationToken);
        return new SePayWebhookStoreResult(
            SePayWebhookStoreOutcome.Fulfilled,
            authoritativeProviderTransaction.Id,
            isReplay);
    }

    private async Task AddDeliveryAndCommitAsync(
        IDbContextTransaction databaseTransaction,
        ProviderTransaction providerTransaction,
        string payloadFingerprint,
        DateTimeOffset receivedAt,
        string processingResult,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        dbContext.WebhookDeliveries.Add(WebhookDelivery.CreateAuthenticated(
            Guid.NewGuid(),
            providerTransaction.Id,
            receivedAt,
            payloadFingerprint,
            processingResult,
            errorCode));
        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);
    }

    private static SePayWebhookStoreOutcome ToStoreOutcome(
        ProviderTransaction transaction) => transaction.ProcessingStatus switch
        {
            ProviderTransactionProcessingStatuses.Fulfilled =>
                SePayWebhookStoreOutcome.Fulfilled,
            ProviderTransactionProcessingStatuses.ReconciliationRequired =>
                SePayWebhookStoreOutcome.ReconciliationRequired,
            _ => SePayWebhookStoreOutcome.Accepted,
        };

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

    private async Task EnsureWalletExistsAsync(
        Guid userId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wallet.wallets
                (user_id, paid_available, paid_reserved, bonus_available, version, created_at, updated_at)
            SELECT id, 0, 0, 0, 1, {createdAt}, {createdAt}
            FROM identity.users
            WHERE id = {userId}
            ON CONFLICT (user_id) DO NOTHING
            """, cancellationToken);
    }

    private Task<WalletAccount?> LockWalletAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        dbContext.Wallets
            .FromSqlInterpolated($"""
                SELECT *
                FROM wallet.wallets
                WHERE user_id = {userId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

    private Task<List<PaidCoinLot>> LockPaidLotsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        dbContext.PaidCoinLots
            .FromSqlInterpolated($"""
                SELECT *
                FROM wallet.paid_coin_lots
                WHERE user_id = {userId}
                ORDER BY created_at, id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

    private Task<PaymentOrder?> LockPaymentOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        dbContext.PaymentOrders
            .FromSqlInterpolated($"""
                SELECT *
                FROM billing.payment_orders
                WHERE id = {orderId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

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
