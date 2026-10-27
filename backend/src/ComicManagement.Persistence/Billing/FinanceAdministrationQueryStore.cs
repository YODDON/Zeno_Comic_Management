using ComicManagement.Shared.Persistence;
using ComicManagement.Administration.Finance;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class FinanceAdministrationQueryStore(
    AppDbContext dbContext,
    IConsistentReadExecutor consistentRead) : IFinanceAdministrationQueryStore
{
    public async Task<AdminFinancePage<AdminPaymentListItem>> ListPaymentsAsync(
        AdminFinanceListRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = from order in dbContext.PaymentOrders.AsNoTracking()
                    join user in dbContext.Users.AsNoTracking() on order.UserId equals user.Id
                    select new { Order = order, User = user };
        if (request.Search is { } search)
        {
            var pattern = $"%{EscapeLike(search)}%";
            query = query.Where(row =>
                EF.Functions.ILike(row.Order.OrderCode, pattern, "\\") ||
                EF.Functions.ILike(row.User.DisplayName, pattern, "\\") ||
                (row.User.Email != null && EF.Functions.ILike(row.User.Email, pattern, "\\")));
        }
        if (request.Status is { } status)
            query = query.Where(row => row.Order.OrderStatus == status);
        if (request.Type is { } type)
            query = query.Where(row => row.Order.OrderType == type);

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(row => row.Order.CreatedAt)
            .ThenByDescending(row => row.Order.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(row => new AdminPaymentListItem(
                row.Order.Id,
                row.Order.OrderCode,
                row.User.Id,
                row.User.DisplayName,
                row.User.Email,
                row.Order.OrderType,
                row.Order.AmountVnd,
                row.Order.PaidCoinsSnapshot,
                row.Order.OrderStatus,
                row.Order.ProviderPaymentStatus,
                row.Order.FulfillmentStatus,
                row.Order.CreatedAt,
                row.Order.ExpiresAt,
                row.Order.VerifiedPaidAt,
                dbContext.ProviderTransactions.AsNoTracking()
                    .Where(transaction => transaction.MatchedOrderId == row.Order.Id)
                    .OrderByDescending(transaction => transaction.VerifiedAt)
                    .ThenByDescending(transaction => transaction.Id)
                    .Select(transaction => transaction.ReconciliationReason)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
        return Page(items, request, total);
    }

    public Task<AdminPaymentDetail?> GetPaymentAsync(
        Guid paymentId,
        CancellationToken cancellationToken = default) =>
        consistentRead.ExecuteAsync(async token =>
        {
            var payment = await (from order in dbContext.PaymentOrders.AsNoTracking()
                                 join user in dbContext.Users.AsNoTracking() on order.UserId equals user.Id
                                 where order.Id == paymentId
                                 select new AdminPaymentListItem(
                                     order.Id,
                                     order.OrderCode,
                                     user.Id,
                                     user.DisplayName,
                                     user.Email,
                                     order.OrderType,
                                     order.AmountVnd,
                                     order.PaidCoinsSnapshot,
                                     order.OrderStatus,
                                     order.ProviderPaymentStatus,
                                     order.FulfillmentStatus,
                                     order.CreatedAt,
                                     order.ExpiresAt,
                                     order.VerifiedPaidAt,
                                     dbContext.ProviderTransactions.AsNoTracking()
                                         .Where(transaction => transaction.MatchedOrderId == order.Id)
                                         .OrderByDescending(transaction => transaction.VerifiedAt)
                                         .ThenByDescending(transaction => transaction.Id)
                                         .Select(transaction => transaction.ReconciliationReason)
                                         .FirstOrDefault()))
                .SingleOrDefaultAsync(token);
            if (payment is null) return null;
            var transactions = await dbContext.ProviderTransactions.AsNoTracking()
                .Where(transaction => transaction.MatchedOrderId == paymentId)
                .OrderByDescending(transaction => transaction.VerifiedAt)
                .ThenByDescending(transaction => transaction.Id)
                .Select(transaction => new AdminProviderTransactionItem(
                    transaction.Id,
                    transaction.Provider,
                    transaction.ProviderTransactionId,
                    transaction.AmountVnd,
                    transaction.Direction,
                    transaction.AccountNumberLast4,
                    transaction.ReferenceCode,
                    transaction.OccurredAt,
                    transaction.VerifiedAt,
                    transaction.ProcessingStatus,
                    transaction.ReconciliationReason))
                .ToListAsync(token);
            return new AdminPaymentDetail(payment, transactions);
        }, cancellationToken);

    public async Task<AdminFinancePage<AdminLedgerItem>> ListLedgerAsync(
        AdminFinanceListRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = from entry in dbContext.WalletEntries.AsNoTracking()
                    join operation in dbContext.WalletOperations.AsNoTracking()
                        on entry.OperationId equals operation.Id
                    join user in dbContext.Users.AsNoTracking() on entry.UserId equals user.Id
                    select new { Entry = entry, Operation = operation, User = user };
        if (request.Search is { } search)
        {
            var pattern = $"%{EscapeLike(search)}%";
            query = query.Where(row =>
                EF.Functions.ILike(row.User.DisplayName, pattern, "\\") ||
                (row.User.Email != null && EF.Functions.ILike(row.User.Email, pattern, "\\")));
        }
        if (request.Status is { } operationType)
            query = query.Where(row => row.Operation.OperationType == operationType);

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(row => row.Entry.CreatedAt)
            .ThenByDescending(row => row.Entry.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(row => new AdminLedgerItem(
                row.Entry.Id,
                row.Operation.Id,
                row.User.Id,
                row.User.DisplayName,
                row.User.Email,
                row.Operation.OperationType,
                row.Operation.SourceType,
                row.Operation.SourceId,
                row.Entry.CoinType,
                row.Entry.Bucket,
                row.Entry.DeltaCoins,
                row.Entry.LegOrder,
                row.Entry.CreatedAt))
            .ToListAsync(cancellationToken);
        return Page(items, request, total);
    }

    public async Task<AdminFinancePage<AdminRefundListItem>> ListRefundsAsync(
        AdminFinanceListRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = from refund in dbContext.RefundRequests.AsNoTracking()
                    join order in dbContext.PaymentOrders.AsNoTracking()
                        on refund.PaymentOrderId equals order.Id
                    join user in dbContext.Users.AsNoTracking() on refund.UserId equals user.Id
                    select new { Refund = refund, Order = order, User = user };
        if (request.Search is { } search)
        {
            var pattern = $"%{EscapeLike(search)}%";
            query = query.Where(row =>
                EF.Functions.ILike(row.Order.OrderCode, pattern, "\\") ||
                EF.Functions.ILike(row.User.DisplayName, pattern, "\\") ||
                (row.User.Email != null && EF.Functions.ILike(row.User.Email, pattern, "\\")));
        }
        if (request.Status is { } status)
            query = query.Where(row => row.Refund.Status == status);

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(row => row.Refund.RequestedAt)
            .ThenByDescending(row => row.Refund.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(row => new AdminRefundListItem(
                row.Refund.Id,
                row.Order.Id,
                row.Order.OrderCode,
                row.User.Id,
                row.User.DisplayName,
                row.User.Email,
                row.Refund.Status,
                row.Refund.OriginalAmountVnd,
                row.Refund.FeeAmountVnd,
                row.Refund.NetRefundVnd,
                row.Refund.RequiredRecoveryCoins,
                row.Refund.RequestedAt,
                row.Refund.Version))
            .ToListAsync(cancellationToken);
        return Page(items, request, total);
    }

    public Task<AdminRefundDetail?> GetRefundAsync(
        Guid refundId,
        CancellationToken cancellationToken = default) =>
        consistentRead.ExecuteAsync(async token =>
        {
            var core = await (from refund in dbContext.RefundRequests.AsNoTracking()
                              join order in dbContext.PaymentOrders.AsNoTracking()
                                  on refund.PaymentOrderId equals order.Id
                              join user in dbContext.Users.AsNoTracking() on refund.UserId equals user.Id
                              join recipient in dbContext.RefundRecipients.AsNoTracking()
                                  on refund.Id equals recipient.RefundRequestId
                              where refund.Id == refundId
                              select new
                              {
                                  Refund = new AdminRefundListItem(
                                      refund.Id, order.Id, order.OrderCode, user.Id,
                                      user.DisplayName, user.Email, refund.Status,
                                      refund.OriginalAmountVnd, refund.FeeAmountVnd,
                                      refund.NetRefundVnd, refund.RequiredRecoveryCoins,
                                      refund.RequestedAt, refund.Version),
                                  refund.Reason,
                                  Recipient = new AdminRefundRecipientItem(
                                      recipient.BankCode,
                                      recipient.AccountNumberMasked,
                                      recipient.AccountHolderMasked),
                              }).SingleOrDefaultAsync(token);
            if (core is null) return null;

            var lot = await dbContext.PaidCoinLots.AsNoTracking()
                .Where(item => item.TopUpOrderId == core.Refund.PaymentOrderId &&
                               item.UserId == core.Refund.UserId)
                .Select(item => new AdminPaidCoinLotItem(
                    item.Id, item.GrantedCoins, item.SpentCoins, item.RefundedCoins,
                    item.ReservedCoins,
                    item.GrantedCoins - item.SpentCoins - item.RefundedCoins - item.ReservedCoins,
                    item.Version))
                .SingleOrDefaultAsync(token);
            var reservation = await dbContext.CoinReservations.AsNoTracking()
                .Where(item => item.RefundRequestId == refundId)
                .Select(item => new AdminRefundReservationItem(
                    item.Id, item.WalletOperationId, item.AmountCoins, item.Status,
                    item.CreatedAt, item.ResolvedAt))
                .SingleOrDefaultAsync(token);
            var transfer = await dbContext.RefundTransferRecords.AsNoTracking()
                .Where(item => item.RefundRequestId == refundId)
                .Select(item => new AdminRefundTransferItem(
                    item.Id, item.BankReference, item.AmountVnd, item.EvidenceReference,
                    item.Status, item.RecordedAt, item.VerifiedAt,
                    item.ConfirmationNote, item.ResolutionOperationId))
                .SingleOrDefaultAsync(token);
            var audits = await dbContext.AuditLogs.AsNoTracking()
                .Where(item => item.TargetType == "RefundRequest" && item.TargetId == refundId)
                .OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.Id)
                .Select(item => new AdminRefundAuditItem(
                    item.Id, item.ActorId, item.Action, item.OccurredAt,
                    item.RedactedChanges))
                .ToListAsync(token);
            return new AdminRefundDetail(
                core.Refund, core.Reason, core.Recipient, lot, reservation, transfer, audits);
        }, cancellationToken);

    private static AdminFinancePage<T> Page<T>(
        IReadOnlyList<T> items,
        AdminFinanceListRequest request,
        long total) => new(
        items,
        request.Page,
        request.PageSize,
        total,
        total == 0 ? 0 : checked((int)((total + request.PageSize - 1) / request.PageSize)));

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
