using System.Data;
using ComicManagement.Billing.Events;
using ComicManagement.Billing.Payments;
using ComicManagement.Shared.Messaging;
using ComicManagement.Membership.Events;
using ComicManagement.Membership.Subscriptions;
using ComicManagement.Wallet.Accounts;
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
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (verifiedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(verifiedAt));
        }

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

            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                existing,
                transaction.PayloadFingerprint,
                verifiedAt,
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
            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                existing,
                transaction.PayloadFingerprint,
                verifiedAt,
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
            var providerTransaction = existing ?? ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                transaction,
                verifiedAt);
            if (existing is null)
            {
                dbContext.ProviderTransactions.Add(providerTransaction);
            }

            providerTransaction.RequireReconciliation(transaction.ReconciliationReason);

            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                providerTransaction,
                transaction.PayloadFingerprint,
                verifiedAt,
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
            !IsSupportedOrderType(orderProjection.OrderType) ||
            orderProjection.OrderStatus != TopUpOrderStatuses.Pending)
        {
            var providerTransaction = existing ?? ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                transaction,
                verifiedAt);
            if (existing is null)
            {
                dbContext.ProviderTransactions.Add(providerTransaction);
            }

            var reason = orderProjection is null
                ? SePayReconciliationReasons.OrderNotFound
                : !IsSupportedOrderType(orderProjection.OrderType)
                    ? SePayReconciliationReasons.UnsupportedOrderType
                    : orderProjection.OrderStatus == TopUpOrderStatuses.Expired
                        ? SePayReconciliationReasons.LatePayment
                        : SePayReconciliationReasons.OrderAlreadyFulfilled;
            providerTransaction.RequireReconciliation(reason, orderProjection?.Id);
            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                providerTransaction,
                transaction.PayloadFingerprint,
                verifiedAt,
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
                ?? throw new InvalidOperationException("Matched payment order no longer exists.");
            var mismatchCheckedAt = await GetAuthoritativeServerTimeAsync(cancellationToken);
            var providerTransaction = existing ?? ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                transaction,
                verifiedAt);
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
                verifiedAt,
                WebhookDeliveryProcessingResults.ReconciliationRequired,
                reason,
                cancellationToken);
            return new SePayWebhookStoreResult(
                SePayWebhookStoreOutcome.ReconciliationRequired,
                providerTransaction.Id,
                isReplay);
        }

        if (orderProjection.OrderType == PaymentOrderTypes.VipPurchase)
        {
            return await FulfillVipPurchaseAsync(
                databaseTransaction,
                transaction,
                verifiedAt,
                orderProjection.Id,
                orderProjection.UserId,
                existing,
                isReplay,
                cancellationToken);
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
            verifiedAt);
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
            var existingSuccessOutbox = await dbContext.OutboxMessages
                .AsNoTracking()
                .AnyAsync(message =>
                    message.EventType == BillingEventTypes.WalletTopUpCompleted &&
                    EF.Functions.JsonContains(
                        message.Payload,
                        $"{{\"paymentOrderId\":\"{order.Id:D}\"}}"),
                    cancellationToken);
            var existingFulfilledProviderTransaction = await dbContext.ProviderTransactions
                .AsNoTracking()
                .AnyAsync(candidate =>
                    candidate.MatchedOrderId == order.Id &&
                    candidate.ProcessingStatus ==
                        ProviderTransactionProcessingStatuses.Fulfilled,
                    cancellationToken);
            var walletReconciled = await IsWalletReconciledAsync(
                wallet,
                paidLots,
                cancellationToken);
            if (existingOperation ||
                paidLots.Any(lot => lot.TopUpOrderId == order.Id) ||
                existingSuccessOutbox ||
                existingFulfilledProviderTransaction ||
                !walletReconciled)
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
                verifiedAt,
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
            verifiedAt,
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

    private async Task<SePayWebhookStoreResult> FulfillVipPurchaseAsync(
        IDbContextTransaction databaseTransaction,
        SePayNormalizedTransaction transaction,
        DateTimeOffset verifiedAt,
        Guid orderId,
        Guid userId,
        ProviderTransaction? existing,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        await AcquireVipSubscriptionLockAsync(userId, cancellationToken);
        var subscription = await LockVipSubscriptionAsync(userId, cancellationToken);
        var order = await LockPaymentOrderAsync(orderId, cancellationToken)
            ?? throw new InvalidOperationException("Matched VIP order no longer exists.");
        await fulfillmentObserver.AfterFinancialLocksAcquiredAsync(
            transaction.ProviderTransactionId,
            order.Id,
            cancellationToken);
        var authoritativeNow = await GetAuthoritativeServerTimeAsync(cancellationToken);

        var providerTransaction = existing ?? ProviderTransaction.CreateSePay(
            Guid.NewGuid(),
            transaction,
            verifiedAt);
        if (existing is null)
        {
            dbContext.ProviderTransactions.Add(providerTransaction);
        }

        string? reconciliationReason = null;
        if (order.OrderType != PaymentOrderTypes.VipPurchase)
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
        else if (!await IsVipFulfillmentStateValidAsync(
                     order,
                     subscription,
                     cancellationToken))
        {
            reconciliationReason = SePayReconciliationReasons.FulfillmentStateConflict;
        }

        if (reconciliationReason is not null)
        {
            providerTransaction.RequireReconciliation(reconciliationReason, order.Id);
            await AddDeliveryAndCommitAsync(
                databaseTransaction,
                providerTransaction,
                transaction.PayloadFingerprint,
                verifiedAt,
                WebhookDeliveryProcessingResults.ReconciliationRequired,
                reconciliationReason,
                cancellationToken);
            return new SePayWebhookStoreResult(
                SePayWebhookStoreOutcome.ReconciliationRequired,
                providerTransaction.Id,
                isReplay);
        }

        var planId = order.VipPlanId ?? throw new InvalidOperationException(
            "A VIP order requires a plan snapshot.");
        var durationHours = order.VipDurationHoursSnapshot ?? throw new InvalidOperationException(
            "A VIP order requires a duration snapshot.");
        var discountPercent = order.VipDiscountPercentSnapshot ??
            throw new InvalidOperationException("A VIP order requires a discount snapshot.");
        VipExtension extension;
        if (subscription is null)
        {
            (subscription, extension) = VipSubscription.Create(
                order.UserId,
                authoritativeNow,
                durationHours);
            dbContext.VipSubscriptions.Add(subscription);
        }
        else
        {
            extension = subscription.Extend(authoritativeNow, durationHours);
        }

        var vipTransaction = VipTransaction.Create(
            Guid.NewGuid(),
            order.Id,
            order.UserId,
            PaymentOrderTypes.VipPurchase,
            subscription.Version,
            extension,
            durationHours,
            authoritativeNow);
        dbContext.VipTransactions.Add(vipTransaction);
        order.FulfillVipPurchase(authoritativeNow);
        providerTransaction.MarkFulfilled(order.Id);
        outboxWriter.Enqueue(
            MembershipEventTypes.VipMembershipActivated,
            new VipMembershipActivatedEvent(
                order.Id,
                providerTransaction.Id,
                vipTransaction.Id,
                order.UserId,
                planId,
                order.AmountVnd,
                durationHours,
                discountPercent,
                extension.EffectiveStart,
                extension.NewExpiry,
                authoritativeNow),
            authoritativeNow);
        await fulfillmentObserver.BeforeFulfillmentCommitAsync(
            transaction.ProviderTransactionId,
            order.Id,
            cancellationToken);

        await AddDeliveryAndCommitAsync(
            databaseTransaction,
            providerTransaction,
            transaction.PayloadFingerprint,
            verifiedAt,
            WebhookDeliveryProcessingResults.Accepted,
            errorCode: null,
            cancellationToken);
        return new SePayWebhookStoreResult(
            SePayWebhookStoreOutcome.Fulfilled,
            providerTransaction.Id,
            isReplay);
    }

    private async Task<bool> IsVipFulfillmentStateValidAsync(
        PaymentOrder order,
        VipSubscription? subscription,
        CancellationToken cancellationToken)
    {
        var existingTransaction = await dbContext.VipTransactions
            .AsNoTracking()
            .AnyAsync(candidate => candidate.PaymentOrderId == order.Id, cancellationToken);
        var existingSuccessOutbox = await dbContext.OutboxMessages
            .AsNoTracking()
            .AnyAsync(message =>
                message.EventType == MembershipEventTypes.VipMembershipActivated &&
                EF.Functions.JsonContains(
                    message.Payload,
                    $"{{\"paymentOrderId\":\"{order.Id:D}\"}}"),
                cancellationToken);
        var existingFulfilledProviderTransaction = await dbContext.ProviderTransactions
            .AsNoTracking()
            .AnyAsync(candidate =>
                candidate.MatchedOrderId == order.Id &&
                candidate.ProcessingStatus == ProviderTransactionProcessingStatuses.Fulfilled,
                cancellationToken);
        if (existingTransaction || existingSuccessOutbox || existingFulfilledProviderTransaction)
        {
            return false;
        }

        var history = await dbContext.VipTransactions
            .AsNoTracking()
            .Where(candidate => candidate.UserId == order.UserId)
            .OrderBy(candidate => candidate.SubscriptionVersion)
            .ToArrayAsync(cancellationToken);
        if (subscription is null)
        {
            return history.Length == 0;
        }

        if (subscription.Version != history.LongLength || history.Length == 0)
        {
            return false;
        }

        VipTransaction? previous = null;
        foreach (var transaction in history)
        {
            var expectedVersion = previous is null
                ? 1L
                : checked(previous.SubscriptionVersion + 1);
            var expectedStart = previous is null
                ? transaction.FulfilledAt
                : previous.NewExpiry > transaction.FulfilledAt
                    ? previous.NewExpiry
                    : transaction.FulfilledAt;
            if (transaction.SubscriptionVersion != expectedVersion ||
                transaction.PreviousExpiry != previous?.NewExpiry ||
                transaction.EffectiveStart != expectedStart ||
                transaction.NewExpiry != expectedStart.AddHours(transaction.DurationHours))
            {
                return false;
            }

            previous = transaction;
        }

        return previous is not null &&
               previous.SubscriptionVersion == subscription.Version &&
               previous.NewExpiry == subscription.ExpiresAt &&
               previous.FulfilledAt == subscription.LastFulfilledAt;
    }

    private async Task AcquireVipSubscriptionLockAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException(
                "A VIP subscription lock requires a database transaction.");
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = $"vip-subscription:{userId:N}";
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private Task<VipSubscription?> LockVipSubscriptionAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        dbContext.VipSubscriptions
            .FromSqlInterpolated($"""
                SELECT *
                FROM membership.vip_subscriptions
                WHERE user_id = {userId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

    private static SePayWebhookStoreOutcome ToStoreOutcome(
        ProviderTransaction transaction) => transaction.ProcessingStatus switch
        {
            ProviderTransactionProcessingStatuses.Fulfilled =>
                SePayWebhookStoreOutcome.Fulfilled,
            ProviderTransactionProcessingStatuses.ReconciliationRequired =>
                SePayWebhookStoreOutcome.ReconciliationRequired,
            _ => SePayWebhookStoreOutcome.Accepted,
        };

    private static bool IsSupportedOrderType(string orderType) =>
        orderType is PaymentOrderTypes.TopUp or PaymentOrderTypes.VipPurchase;

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

    private async Task<bool> IsWalletReconciledAsync(
        WalletAccount wallet,
        IReadOnlyCollection<PaidCoinLot> paidLots,
        CancellationToken cancellationToken)
    {
        var paidLotAvailable = paidLots.Sum(lot =>
            (decimal)lot.GrantedCoins - lot.SpentCoins - lot.RefundedCoins - lot.ReservedCoins);
        var paidLotReserved = paidLots.Sum(lot => (decimal)lot.ReservedCoins);
        var ledger = await ReadLedgerTotalsAsync(wallet.UserId, cancellationToken);
        return WalletIntegrityPolicy.IsReconciled(new WalletIntegritySnapshot(
            wallet.PaidAvailable,
            wallet.PaidReserved,
            wallet.BonusAvailable,
            paidLotAvailable,
            paidLotReserved,
            ledger.PaidAvailable,
            ledger.PaidReserved,
            ledger.BonusAvailable));
    }

    private async Task<LedgerTotals> ReadLedgerTotalsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException(
                "Ledger reconciliation requires a database transaction.");
        command.CommandText =
            """
            SELECT
                COALESCE(SUM(delta_coins) FILTER (
                    WHERE coin_type = 'Paid' AND bucket = 'Available'), 0),
                COALESCE(SUM(delta_coins) FILTER (
                    WHERE coin_type = 'Paid' AND bucket = 'Reserved'), 0),
                COALESCE(SUM(delta_coins) FILTER (
                    WHERE coin_type = 'Bonus' AND bucket = 'Available'), 0)
            FROM wallet.wallet_entries
            WHERE user_id = @user_id;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "user_id";
        parameter.Value = userId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("PostgreSQL did not return ledger totals.");
        }

        return new LedgerTotals(
            reader.GetFieldValue<decimal>(0),
            reader.GetFieldValue<decimal>(1),
            reader.GetFieldValue<decimal>(2));
    }

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

    private readonly record struct LedgerTotals(
        decimal PaidAvailable,
        decimal PaidReserved,
        decimal BonusAvailable);
}
