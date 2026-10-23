using System.Data;
using ComicManagement.Administration.Auditing;
using ComicManagement.Billing.Events;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ComicManagement.Persistence;

internal sealed partial class RefundAdministrationStore
{
    public async Task<RefundAdministrationResult> RecordTransferAsync(
        RecordRefundTransferStoreRequest command,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await AcquirePayoutIdempotencyLockAsync(
            command.ActorId, "RecordTransfer", command.IdempotencyKey, cancellationToken);
        var existing = await dbContext.RefundTransferRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RecordedBy == command.ActorId &&
                item.IdempotencyKey == command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var replay = existing.RefundRequestId == command.RefundId &&
                         existing.RequestFingerprint == command.RequestFingerprint
                ? await TransferResultAsync(existing, cancellationToken)
                : Result(RefundAdministrationOutcome.IdempotencyConflict);
            if (replay.Outcome == RefundAdministrationOutcome.Succeeded)
                await transaction.CommitAsync(cancellationToken);
            else
                await transaction.RollbackAsync(cancellationToken);
            return replay;
        }

        var request = await LockRefundRequestAsync(command.RefundId, cancellationToken);
        if (request is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.NotFound);
        }
        if (request.Status != RefundRequestStatuses.RecoveryReserved)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.StateConflict);
        }
        if (request.Version != command.ExpectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.ConcurrencyConflict);
        }

        var reservation = await dbContext.CoinReservations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RefundRequestId == request.Id,
                cancellationToken);
        if (reservation is null || reservation.Status != CoinReservationStatuses.Reserved ||
            reservation.AmountCoins != request.RequiredRecoveryCoins)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IntegrityConflict);
        }

        var serverNow = await GetServerTimeAsync(cancellationToken);
        var transfer = RefundTransferRecord.Create(
            Guid.NewGuid(), request.Id, request.UserId, command.BankReference,
            request.NetRefundVnd, command.EvidenceReference, command.ReportedOutcome,
            command.ActorId, serverNow, command.IdempotencyKey, command.RequestFingerprint);
        request.RecordTransferAttempt(command.ReportedOutcome, serverNow);
        dbContext.RefundTransferRecords.Add(transfer);
        AddPayoutAudit(command.ActorId, "Billing.RefundTransferRecorded", request,
            command.CorrelationId, serverNow, transfer.Id, null);
        await payoutObserver.AfterTransferRecordPreparedAsync(
            request.Id, transfer.BankReference, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(
                   exception, "ux_refund_transfer_records_bank_reference"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.TransferEvidenceConflict);
        }
        return Result(RefundAdministrationOutcome.Succeeded, request, reservation.Id,
            reservation.WalletOperationId, transfer.Id, transfer.Status);
    }

    public async Task<RefundAdministrationResult> ResolveTransferAsync(
        ResolveRefundTransferStoreRequest command,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await AcquirePayoutIdempotencyLockAsync(command.ActorId,
            command.Succeeded ? "Complete" : "Release", command.IdempotencyKey,
            cancellationToken);
        var existing = await dbContext.RefundTransferRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.VerifiedBy == command.ActorId &&
                item.VerificationIdempotencyKey == command.IdempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            var expectedStatus = command.Succeeded
                ? RefundTransferStatuses.VerifiedSucceeded
                : RefundTransferStatuses.VerifiedFailed;
            if (existing.RefundRequestId != command.RefundId ||
                existing.Id != command.TransferRecordId ||
                existing.VerificationFingerprint != command.RequestFingerprint ||
                existing.Status != expectedStatus)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result(RefundAdministrationOutcome.IdempotencyConflict);
            }
            var replay = await TransferResultAsync(existing, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var identity = await dbContext.RefundRequests.AsNoTracking()
            .Where(item => item.Id == command.RefundId)
            .Select(item => new { item.UserId, item.PaymentOrderId })
            .SingleOrDefaultAsync(cancellationToken);
        if (identity is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.NotFound);
        }

        var wallet = await LockWalletAsync(identity.UserId, cancellationToken);
        var lot = await LockOriginalLotAsync(identity.PaymentOrderId, identity.UserId,
            cancellationToken);
        var request = await LockRefundRequestAsync(command.RefundId, cancellationToken);
        var reservation = await LockReservationAsync(command.RefundId, cancellationToken);
        var transfer = await LockTransferAsync(command.TransferRecordId, cancellationToken);
        if (wallet is null || lot is null || request is null || reservation is null ||
            transfer is null || transfer.RefundRequestId != request.Id ||
            transfer.UserId != request.UserId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IntegrityConflict);
        }

        await payoutObserver.AfterFinancialLocksAcquiredAsync(request.Id, cancellationToken);
        if (request.Version != command.ExpectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.ConcurrencyConflict);
        }
        if (request.Status is not (RefundRequestStatuses.TransferPending or
                RefundRequestStatuses.NeedsReconciliation) ||
            reservation.Status != CoinReservationStatuses.Reserved ||
            reservation.AmountCoins != request.RequiredRecoveryCoins ||
            reservation.PaidCoinLotId != lot.Id || transfer.AmountVnd != request.NetRefundVnd)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.StateConflict);
        }

        var expectedTransferStatus = command.Succeeded
            ? RefundTransferStatuses.ReportedSucceeded
            : RefundTransferStatuses.ReportedFailed;
        if (transfer.Status != expectedTransferStatus &&
            transfer.Status != RefundTransferStatuses.NeedsReconciliation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.TransferNotVerified);
        }

        var authoritative = await financialLoader.LoadForFinancialProcessingAsync(
            request.Id, cancellationToken);
        if (authoritative.Outcome != RefundFinancialLoadOutcome.Succeeded ||
            authoritative.State is not { } financial ||
            financial.UserId != wallet.UserId ||
            financial.OriginalPaidCoinLotId != lot.Id ||
            financial.OrderType != PaymentOrderTypes.TopUp ||
            financial.OrderStatus != TopUpOrderStatuses.Succeeded ||
            financial.ProviderPaymentStatus != ProviderPaymentStatuses.Verified ||
            financial.FulfillmentStatus != PaymentFulfillmentStatuses.Fulfilled ||
            financial.LotSpentCoins != 0 || financial.LotRefundedCoins != 0 ||
            financial.LotReservedCoins != reservation.AmountCoins ||
            lot.ReservedCoins != reservation.AmountCoins ||
            !await WalletIsReconciledAsync(wallet, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IntegrityConflict);
        }

        var serverNow = await GetServerTimeAsync(cancellationToken);
        var operation = WalletOperation.Create(Guid.NewGuid(), request.UserId,
            command.Succeeded ? WalletOperationTypes.RefundComplete : WalletOperationTypes.RefundRelease,
            WalletOperationSourceTypes.RefundRequest, request.Id, null, serverNow);
        try
        {
            if (command.Succeeded)
            {
                wallet.ConsumeReservedPaid(reservation.AmountCoins, serverNow);
                lot.RefundReserved(reservation.AmountCoins);
                reservation.Consume(serverNow);
                request.Complete(command.ActorId, command.ExpectedVersion, serverNow);
            }
            else
            {
                wallet.ReleaseReservedPaid(reservation.AmountCoins, serverNow);
                lot.ReleaseReserved(reservation.AmountCoins);
                reservation.Release(serverNow);
                request.ConfirmTransferFailed(command.ActorId, command.ExpectedVersion, serverNow);
                request.MarkReservationReleased(serverNow);
            }
            transfer.Verify(command.Succeeded, command.ActorId, command.ConfirmationNote,
                command.IdempotencyKey, command.RequestFingerprint, operation.Id, serverNow);
        }
        catch (RefundRequestConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.ConcurrencyConflict);
        }
        catch (InvalidOperationException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IntegrityConflict);
        }

        dbContext.WalletOperations.Add(operation);
        if (command.Succeeded)
        {
            dbContext.WalletEntries.Add(WalletEntry.Create(Guid.NewGuid(), operation,
                WalletCoinTypes.Paid, WalletBuckets.Reserved, -reservation.AmountCoins,
                1, serverNow));
            outboxWriter.Enqueue(BillingEventTypes.RefundCompleted,
                new RefundCompletedEvent(request.Id, request.UserId, request.PaymentOrderId,
                    lot.Id, transfer.Id, operation.Id, reservation.AmountCoins,
                    request.NetRefundVnd, serverNow), serverNow);
        }
        else
        {
            dbContext.WalletEntries.AddRange(
                WalletEntry.Create(Guid.NewGuid(), operation, WalletCoinTypes.Paid,
                    WalletBuckets.Reserved, -reservation.AmountCoins, 1, serverNow),
                WalletEntry.Create(Guid.NewGuid(), operation, WalletCoinTypes.Paid,
                    WalletBuckets.Available, reservation.AmountCoins, 2, serverNow));
            outboxWriter.Enqueue(BillingEventTypes.RefundReservationReleased,
                new RefundReservationReleasedEvent(request.Id, request.UserId,
                    request.PaymentOrderId, lot.Id, transfer.Id, operation.Id,
                    reservation.AmountCoins, serverNow), serverNow);
        }
        AddPayoutAudit(command.ActorId,
            command.Succeeded ? "Billing.RefundCompleted" : "Billing.RefundReservationReleased",
            request, command.CorrelationId, serverNow, transfer.Id, operation.Id);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(
                   exception, "ux_refund_transfer_records_verify_idempotency"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(RefundAdministrationOutcome.IdempotencyConflict);
        }
        return Result(RefundAdministrationOutcome.Succeeded, request, reservation.Id,
            reservation.WalletOperationId, transfer.Id, transfer.Status, operation.Id);
    }

    private Task<CoinReservation?> LockReservationAsync(Guid refundId,
        CancellationToken cancellationToken) => dbContext.CoinReservations
        .FromSqlInterpolated($"SELECT * FROM wallet.coin_reservations WHERE refund_request_id = {refundId} FOR UPDATE")
        .SingleOrDefaultAsync(cancellationToken);

    private Task<RefundTransferRecord?> LockTransferAsync(Guid transferId,
        CancellationToken cancellationToken) => dbContext.RefundTransferRecords
        .FromSqlInterpolated($"SELECT * FROM billing.refund_transfer_records WHERE id = {transferId} FOR UPDATE")
        .SingleOrDefaultAsync(cancellationToken);

    private async Task<RefundAdministrationResult> TransferResultAsync(
        RefundTransferRecord transfer, CancellationToken cancellationToken)
    {
        var request = await dbContext.RefundRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == transfer.RefundRequestId, cancellationToken);
        var reservation = await dbContext.CoinReservations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RefundRequestId == transfer.RefundRequestId,
                cancellationToken);
        return request is null || reservation is null
            ? Result(RefundAdministrationOutcome.IntegrityConflict)
            : Result(RefundAdministrationOutcome.Succeeded, request, reservation.Id,
                reservation.WalletOperationId, transfer.Id, transfer.Status,
                transfer.ResolutionOperationId);
    }

    private async Task AcquirePayoutIdempotencyLockAsync(Guid actorId, string operation,
        string key, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Refund payout idempotency requires a transaction.");
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = $"{actorId:N}:Refund{operation}:{key}";
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: var actualConstraint,
        } && string.Equals(actualConstraint, constraintName, StringComparison.Ordinal);

    private void AddPayoutAudit(Guid actorId, string action, RefundRequest request,
        string correlationId, DateTimeOffset occurredAt, Guid transferId, Guid? operationId) =>
        dbContext.AuditLogs.Add(AuditLog.Create(Guid.NewGuid(), actorId, action,
            "RefundRequest", request.Id, correlationId, occurredAt,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                request.Status,
                request.Version,
                TransferRecordId = transferId,
                WalletOperationId = operationId,
            })));
}
