using System.Data;
using System.Security.Cryptography;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ComicManagement.Persistence;

internal interface IRefundRequestCriticalSectionObserver
{
    Task AfterIdempotencyLockAcquiredAsync(
        Guid userId,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class NoOpRefundRequestCriticalSectionObserver :
    IRefundRequestCriticalSectionObserver
{
    public Task AfterIdempotencyLockAcquiredAsync(
        Guid userId,
        string idempotencyKey,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RefundRequestStore(
    AppDbContext dbContext,
    IRefundRequestCriticalSectionObserver criticalSectionObserver) : IRefundRequestStore
{
    public async Task<RefundEligibilityResult> GetEligibilityAsync(
        Guid userId,
        Guid paymentOrderId,
        CancellationToken cancellationToken = default)
    {
        var order = await dbContext.PaymentOrders.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == paymentOrderId && item.UserId == userId,
                cancellationToken);
        if (order is null)
        {
            return new RefundEligibilityResult(RefundRequestOutcome.NotFound);
        }

        var lot = await dbContext.PaidCoinLots.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TopUpOrderId == paymentOrderId &&
                                          item.UserId == userId,
                cancellationToken);
        var alreadyExists = await dbContext.RefundRequests.AsNoTracking()
            .AnyAsync(item => item.PaymentOrderId == paymentOrderId, cancellationToken);
        var serverNow = await GetAuthoritativeServerTimeAsync(cancellationToken);
        return RefundEligibilityPolicy.Evaluate(
            CreateEligibilitySnapshot(order, lot, alreadyExists),
            serverNow);
    }

    public async Task<RefundRequestResult> CreateOrReplayAsync(
        CreateRefundStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireIdempotencyLockAsync(request.UserId, request.IdempotencyKey,
            cancellationToken);
        await criticalSectionObserver.AfterIdempotencyLockAcquiredAsync(
            request.UserId,
            request.IdempotencyKey,
            cancellationToken);

        var existingByKey = await dbContext.RefundRequests
            .FromSqlInterpolated($"""
                SELECT *
                FROM billing.refund_requests
                WHERE user_id = {request.UserId}
                  AND idempotency_key = {request.IdempotencyKey}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingByKey is not null)
        {
            var replayRecipient = await dbContext.RefundRecipients.AsNoTracking()
                .SingleAsync(item => item.RefundRequestId == existingByKey.Id,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return request.Fingerprints.CandidatesByVersion.TryGetValue(
                       existingByKey.RequestFingerprintVersion,
                       out var candidateFingerprint) &&
                   CryptographicOperations.FixedTimeEquals(
                       Convert.FromHexString(existingByKey.RequestFingerprint),
                       Convert.FromHexString(candidateFingerprint))
                ? new RefundRequestResult(
                    RefundRequestOutcome.Succeeded,
                    MapState(existingByKey, replayRecipient))
                : new RefundRequestResult(RefundRequestOutcome.IdempotencyConflict);
        }

        var order = await dbContext.PaymentOrders
            .FromSqlInterpolated($"""
                SELECT *
                FROM billing.payment_orders
                WHERE id = {request.PaymentOrderId}
                  AND user_id = {request.UserId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RefundRequestResult(RefundRequestOutcome.NotFound);
        }

        var lot = await dbContext.PaidCoinLots
            .FromSqlInterpolated($"""
                SELECT *
                FROM wallet.paid_coin_lots
                WHERE topup_order_id = {request.PaymentOrderId}
                  AND user_id = {request.UserId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        var alreadyExists = await dbContext.RefundRequests
            .AnyAsync(item => item.PaymentOrderId == request.PaymentOrderId,
                cancellationToken);
        var serverNow = await GetAuthoritativeServerTimeAsync(cancellationToken);
        var eligibility = RefundEligibilityPolicy.Evaluate(
            CreateEligibilitySnapshot(order, lot, alreadyExists),
            serverNow);
        if (eligibility.Outcome != RefundRequestOutcome.Succeeded ||
            eligibility.State is not { Eligible: true } state)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RefundRequestResult(eligibility.Outcome);
        }

        var refund = RefundRequest.Create(
            request.RefundId,
            request.PaymentOrderId,
            request.UserId,
            request.Reason,
            state.OriginalAmountVnd,
            state.LotGrantedCoins,
            request.IdempotencyKey,
            request.Fingerprints.CurrentFingerprint,
            request.Fingerprints.CurrentVersion,
            serverNow);
        var recipient = RefundRecipient.Create(
            request.RecipientId,
            request.RefundId,
            request.BankCode,
            request.ProtectedRecipient.AccountNumberCiphertext,
            request.ProtectedRecipient.AccountHolderCiphertext,
            request.AccountNumberLast4,
            request.AccountNumberMasked,
            request.AccountHolderMasked,
            request.ProtectedRecipient.EncryptionKeyVersion,
            serverNow);
        dbContext.RefundRequests.Add(refund);
        dbContext.RefundRecipients.Add(recipient);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RefundRequestResult(
            RefundRequestOutcome.Succeeded,
            MapState(refund, recipient));
    }

    public async Task<RefundRequestState?> GetAsync(
        Guid userId,
        Guid refundId,
        CancellationToken cancellationToken = default)
    {
        var result = await (from request in dbContext.RefundRequests.AsNoTracking()
                            join recipient in dbContext.RefundRecipients.AsNoTracking()
                                on request.Id equals recipient.RefundRequestId
                            where request.Id == refundId && request.UserId == userId
                            select new { Request = request, Recipient = recipient })
            .SingleOrDefaultAsync(cancellationToken);
        return result is null ? null : MapState(result.Request, result.Recipient);
    }

    public async Task<IReadOnlyList<RefundRequestState>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (from request in dbContext.RefundRequests.AsNoTracking()
                          join recipient in dbContext.RefundRecipients.AsNoTracking()
                              on request.Id equals recipient.RefundRequestId
                          where request.UserId == userId
                          orderby request.RequestedAt descending, request.Id descending
                          select new { Request = request, Recipient = recipient })
            .Take(100)
            .ToListAsync(cancellationToken);
        return rows.Select(row => MapState(row.Request, row.Recipient)).ToArray();
    }

    private static RefundEligibilitySnapshot CreateEligibilitySnapshot(
        PaymentOrder order,
        PaidCoinLot? lot,
        bool alreadyExists) => new(
            order.Id,
            order.OrderType,
            order.OrderStatus,
            order.ProviderPaymentStatus,
            order.FulfillmentStatus,
            order.AmountVnd,
            order.PaidCoinsSnapshot,
            order.VerifiedPaidAt,
            lot?.GrantedCoins,
            lot?.SpentCoins,
            lot?.RefundedCoins,
            lot?.ReservedCoins,
            alreadyExists);

    private static RefundRequestState MapState(
        RefundRequest request,
        RefundRecipient recipient) => new(
            request.Id,
            request.PaymentOrderId,
            request.Status,
            request.Reason,
            request.OriginalAmountVnd,
            request.FeeAmountVnd,
            request.NetRefundVnd,
            request.RequiredRecoveryCoins,
            request.RequestedAt,
            recipient.BankCode,
            recipient.AccountNumberMasked,
            recipient.AccountHolderMasked);

    private async Task AcquireIdempotencyLockAsync(
        Guid userId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A refund idempotency lock requires a transaction.");
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = $"{userId:N}:RefundRequest:{idempotencyKey}";
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<DateTimeOffset> GetAuthoritativeServerTimeAsync(
        CancellationToken cancellationToken)
    {
        return (await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken))
            .ToUniversalTime();
    }
}
