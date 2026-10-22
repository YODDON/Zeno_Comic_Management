using System.Data;
using System.Text.Json;
using ComicManagement.Administration.Auditing;
using ComicManagement.Billing.Events;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Shared.Messaging;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence;

internal interface IRefundApprovalCriticalSectionObserver
{
    Task AfterFinancialLocksAcquiredAsync(
        Guid refundRequestId,
        CancellationToken cancellationToken);
}

internal sealed class NoOpRefundApprovalCriticalSectionObserver :
    IRefundApprovalCriticalSectionObserver
{
    public Task AfterFinancialLocksAcquiredAsync(
        Guid refundRequestId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal interface IRefundPayoutCriticalSectionObserver
{
    Task AfterFinancialLocksAcquiredAsync(Guid refundRequestId,
        CancellationToken cancellationToken);
}

internal sealed class NoOpRefundPayoutCriticalSectionObserver :
    IRefundPayoutCriticalSectionObserver
{
    public Task AfterFinancialLocksAcquiredAsync(Guid refundRequestId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed partial class RefundAdministrationStore(
    AppDbContext dbContext,
    IRefundFinancialProcessingLoader financialLoader,
    IOutboxWriter outboxWriter,
    IRefundApprovalCriticalSectionObserver observer,
    IRefundPayoutCriticalSectionObserver payoutObserver) : IRefundAdministrationStore
{
    public async Task<RefundAdministrationResult> ReviewAsync(
        ReviewRefundStoreRequest command,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var request = await LockRefundRequestAsync(command.RefundId, cancellationToken);
        if (request is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.NotFound);
        }

        if (request.Status == RefundRequestStatuses.UnderReview)
        {
            await transaction.CommitAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.Succeeded, request);
        }

        if (request.Status != RefundRequestStatuses.Requested)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.StateConflict);
        }

        var serverNow = await GetServerTimeAsync(cancellationToken);
        request.BeginReview(command.ActorId, serverNow);
        AddAudit(command.ActorId, "Billing.RefundReviewStarted", request,
            command.CorrelationId, serverNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result(RefundAdministrationOutcome.Succeeded, request);
    }

    public async Task<RefundAdministrationResult> ApproveAsync(
        ApproveRefundStoreRequest command,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await AcquireApprovalIdempotencyLockAsync(
            command.ActorId,
            command.IdempotencyKey,
            cancellationToken);
        var existingByKey = await dbContext.CoinReservations.AsNoTracking()
            .SingleOrDefaultAsync(reservation =>
                reservation.ApprovedBy == command.ActorId &&
                reservation.IdempotencyKey == command.IdempotencyKey,
                cancellationToken);
        if (existingByKey is not null)
        {
            if (existingByKey.RefundRequestId != command.RefundId ||
                existingByKey.RequestFingerprint != command.RequestFingerprint)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result(RefundAdministrationOutcome.IdempotencyConflict);
            }

            var replay = ValidateReplay(command, existingByKey);
            if (replay.Outcome == RefundAdministrationOutcome.Succeeded)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return replay;
        }

        var identity = await dbContext.RefundRequests.AsNoTracking()
            .Where(request => request.Id == command.RefundId)
            .Select(request => new { request.UserId, request.PaymentOrderId })
            .SingleOrDefaultAsync(cancellationToken);
        if (identity is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.NotFound);
        }

        var wallet = await LockWalletAsync(identity.UserId, cancellationToken);
        if (wallet is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IntegrityConflict);
        }

        var lot = await LockOriginalLotAsync(
            identity.PaymentOrderId,
            identity.UserId,
            cancellationToken);
        var request = await LockRefundRequestAsync(command.RefundId, cancellationToken);
        if (lot is null || request is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IntegrityConflict);
        }

        await observer.AfterFinancialLocksAcquiredAsync(command.RefundId, cancellationToken);

        var existingReservation = await dbContext.CoinReservations.AsNoTracking()
            .SingleOrDefaultAsync(
                reservation => reservation.RefundRequestId == command.RefundId,
                cancellationToken);
        if (existingReservation is not null ||
            request.Status == RefundRequestStatuses.RecoveryReserved)
        {
            var replay = ValidateReplay(command, existingReservation);
            if (replay.Outcome == RefundAdministrationOutcome.Succeeded)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return replay;
        }

        if (request.Status != RefundRequestStatuses.UnderReview)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.StateConflict);
        }

        if (request.Version != command.ExpectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.ConcurrencyConflict);
        }

        var authoritative = await financialLoader.LoadForFinancialProcessingAsync(
            command.RefundId,
            cancellationToken);
        if (authoritative.Outcome != RefundFinancialLoadOutcome.Succeeded ||
            authoritative.State is not { } financial ||
            financial.OriginalPaidCoinLotId != lot.Id ||
            financial.UserId != wallet.UserId ||
            financial.OrderType != PaymentOrderTypes.TopUp ||
            financial.OrderStatus != TopUpOrderStatuses.Succeeded ||
            financial.ProviderPaymentStatus != ProviderPaymentStatuses.Verified ||
            financial.FulfillmentStatus != PaymentFulfillmentStatuses.Fulfilled)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IntegrityConflict);
        }

        if (!financial.IsFullyRecoverable ||
            lot.SpentCoins != 0 || lot.RefundedCoins != 0 || lot.ReservedCoins != 0 ||
            lot.AvailableCoins != request.RequiredRecoveryCoins)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.RecoveryInsufficient);
        }

        if (!await WalletIsReconciledAsync(wallet, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IntegrityConflict);
        }

        var serverNow = await GetServerTimeAsync(cancellationToken);
        var operation = WalletOperation.Create(
            Guid.NewGuid(),
            request.UserId,
            WalletOperationTypes.RefundReserve,
            WalletOperationSourceTypes.RefundRequest,
            request.Id,
            idempotencyKey: null,
            serverNow);
        try
        {
            wallet.ReservePaid(request.RequiredRecoveryCoins, serverNow);
            lot.Reserve(request.RequiredRecoveryCoins);
            request.ReserveRecovery(
                command.ActorId,
                command.DecisionNote,
                command.ExpectedVersion,
                serverNow);
        }
        catch (InvalidOperationException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.RecoveryInsufficient);
        }
        catch (RefundRequestConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.ConcurrencyConflict);
        }

        var reservation = CoinReservation.Create(
            Guid.NewGuid(),
            request.Id,
            request.UserId,
            lot.Id,
            operation.Id,
            command.ActorId,
            command.IdempotencyKey,
            command.RequestFingerprint,
            request.RequiredRecoveryCoins,
            request.Version,
            request.ReviewedBy!.Value,
            request.ReviewedAt!.Value,
            command.DecisionNote,
            serverNow);

        dbContext.WalletOperations.Add(operation);
        dbContext.WalletEntries.AddRange(
            WalletEntry.Create(
                Guid.NewGuid(), operation, WalletCoinTypes.Paid, WalletBuckets.Available,
                -request.RequiredRecoveryCoins, 1, serverNow),
            WalletEntry.Create(
                Guid.NewGuid(), operation, WalletCoinTypes.Paid, WalletBuckets.Reserved,
                request.RequiredRecoveryCoins, 2, serverNow));
        dbContext.CoinReservations.Add(reservation);
        outboxWriter.Enqueue(
            BillingEventTypes.RefundRecoveryReserved,
            new RefundRecoveryReservedEvent(
                request.Id,
                request.UserId,
                request.PaymentOrderId,
                lot.Id,
                operation.Id,
                request.RequiredRecoveryCoins,
                serverNow),
            serverNow);
        AddAudit(command.ActorId, "Billing.RefundRecoveryReserved", request,
            command.CorrelationId, serverNow);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result(
            RefundAdministrationOutcome.Succeeded,
            request,
            reservation.Id,
            operation.Id);
    }

    public async Task<RefundAdministrationResult> RejectAsync(
        RejectRefundStoreRequest command,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var request = await LockRefundRequestAsync(command.RefundId, cancellationToken);
        if (request is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.NotFound);
        }

        if (request.Status == RefundRequestStatuses.Rejected &&
            request.Version == command.ExpectedVersion + 1 &&
            request.DecisionReason == command.Reason)
        {
            await transaction.CommitAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.Succeeded, request);
        }

        if (request.Status != RefundRequestStatuses.UnderReview)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.StateConflict);
        }

        var serverNow = await GetServerTimeAsync(cancellationToken);
        try
        {
            request.Reject(command.ActorId, command.Reason, command.ExpectedVersion, serverNow);
        }
        catch (RefundRequestConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.ConcurrencyConflict);
        }
        catch (InvalidOperationException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.StateConflict);
        }

        AddAudit(command.ActorId, "Billing.RefundRejected", request,
            command.CorrelationId, serverNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result(RefundAdministrationOutcome.Succeeded, request);
    }

    private static RefundAdministrationResult ValidateReplay(
        ApproveRefundStoreRequest command,
        CoinReservation? reservation)
    {
        if (reservation is null ||
            reservation.RefundRequestId != command.RefundId ||
            reservation.ApprovedBy != command.ActorId ||
            reservation.IdempotencyKey != command.IdempotencyKey ||
            reservation.RequestFingerprint != command.RequestFingerprint ||
            reservation.ApprovalResultVersion != command.ExpectedVersion + 1 ||
            reservation.ApprovalResultDecisionNote != command.DecisionNote ||
            reservation.ApprovalResultStatus != RefundApprovalResultStatuses.RecoveryReserved)
        {
            return Result(RefundAdministrationOutcome.StateConflict);
        }

        return new RefundAdministrationResult(
            RefundAdministrationOutcome.Succeeded,
            new RefundAdministrationState(
                reservation.RefundRequestId,
                reservation.ApprovalResultStatus,
                reservation.AmountCoins,
                reservation.ApprovalResultVersion,
                reservation.ApprovalResultReviewedBy,
                reservation.ApprovalResultReviewedAt,
                reservation.ApprovedBy,
                reservation.CreatedAt,
                reservation.ApprovalResultDecisionNote,
                reservation.Id,
                reservation.WalletOperationId));
    }

    private Task<RefundRequest?> LockRefundRequestAsync(
        Guid refundRequestId,
        CancellationToken cancellationToken) => dbContext.RefundRequests
        .FromSqlInterpolated($"""
            SELECT * FROM billing.refund_requests
            WHERE id = {refundRequestId}
            FOR UPDATE
            """)
        .SingleOrDefaultAsync(cancellationToken);

    private Task<WalletAccount?> LockWalletAsync(
        Guid userId,
        CancellationToken cancellationToken) => dbContext.Wallets
        .FromSqlInterpolated($"""
            SELECT * FROM wallet.wallets
            WHERE user_id = {userId}
            FOR UPDATE
            """)
        .SingleOrDefaultAsync(cancellationToken);

    private Task<PaidCoinLot?> LockOriginalLotAsync(
        Guid paymentOrderId,
        Guid userId,
        CancellationToken cancellationToken) => dbContext.PaidCoinLots
        .FromSqlInterpolated($"""
            SELECT * FROM wallet.paid_coin_lots
            WHERE topup_order_id = {paymentOrderId} AND user_id = {userId}
            FOR UPDATE
            """)
        .SingleOrDefaultAsync(cancellationToken);

    private async Task<bool> WalletIsReconciledAsync(
        WalletAccount wallet,
        CancellationToken cancellationToken)
    {
        var lotAvailable = await dbContext.PaidCoinLots.AsNoTracking()
            .Where(lot => lot.UserId == wallet.UserId)
            .SumAsync(lot => (decimal)lot.GrantedCoins - lot.SpentCoins -
                             lot.RefundedCoins - lot.ReservedCoins, cancellationToken);
        var lotReserved = await dbContext.PaidCoinLots.AsNoTracking()
            .Where(lot => lot.UserId == wallet.UserId)
            .SumAsync(lot => (decimal)lot.ReservedCoins, cancellationToken);
        var ledgerAvailable = await SumLedgerAsync(
            wallet.UserId, WalletBuckets.Available, cancellationToken);
        var ledgerReserved = await SumLedgerAsync(
            wallet.UserId, WalletBuckets.Reserved, cancellationToken);
        return lotAvailable == wallet.PaidAvailable &&
               lotReserved == wallet.PaidReserved &&
               ledgerAvailable == wallet.PaidAvailable &&
               ledgerReserved == wallet.PaidReserved;
    }

    private Task<decimal> SumLedgerAsync(
        Guid userId,
        string bucket,
        CancellationToken cancellationToken) => dbContext.WalletEntries.AsNoTracking()
        .Where(entry => entry.UserId == userId &&
                        entry.CoinType == WalletCoinTypes.Paid &&
                        entry.Bucket == bucket)
        .SumAsync(entry => (decimal)entry.DeltaCoins, cancellationToken);

    private Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<DateTimeOffset>(
                "SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);

    private async Task AcquireApprovalIdempotencyLockAsync(
        Guid actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException(
                "Refund approval idempotency requires a transaction.");
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = $"{actorId:N}:RefundApproval:{idempotencyKey}";
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private void AddAudit(
        Guid actorId,
        string action,
        RefundRequest request,
        string correlationId,
        DateTimeOffset occurredAt) => dbContext.AuditLogs.Add(AuditLog.Create(
        Guid.NewGuid(),
        actorId,
        action,
        "RefundRequest",
        request.Id,
        correlationId,
        occurredAt,
        JsonSerializer.Serialize(new
        {
            request.Status,
            request.Version,
            request.RequiredRecoveryCoins,
        })));

    private static RefundAdministrationResult Result(
        RefundAdministrationOutcome outcome,
        RefundRequest? request = null,
        Guid? reservationId = null,
        Guid? operationId = null,
        Guid? transferRecordId = null,
        string? transferStatus = null,
        Guid? resolutionOperationId = null) => new(
        outcome,
        request is null
            ? null
            : new RefundAdministrationState(
                request.Id,
                request.Status,
                request.RequiredRecoveryCoins,
                request.Version,
                request.ReviewedBy,
                request.ReviewedAt,
                request.ApprovedBy,
                request.ApprovedAt,
                request.DecisionReason,
                reservationId,
                operationId,
                transferRecordId,
                transferStatus,
                resolutionOperationId));
}
