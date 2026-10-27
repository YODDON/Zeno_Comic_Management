namespace ComicManagement.Administration.Finance;

public static class AdminFinanceContractValues
{
    public static readonly IReadOnlyList<string> PaymentOrderStatuses =
        ["Pending", "Expired", "Succeeded"];

    public static readonly IReadOnlyList<string> PaymentOrderTypes =
        ["TopUp", "VipPurchase"];

    public static readonly IReadOnlyList<string> RefundStatuses =
    [
        "Requested",
        "UnderReview",
        "RecoveryReserved",
        "TransferPending",
        "Completed",
        "Rejected",
        "NeedsReconciliation",
        "ReservationReleased",
    ];

    public static readonly IReadOnlyList<string> WalletOperationTypes =
    [
        "TopUpCredit",
        "ChapterUnlock",
        "MissionReward",
        "RefundReserve",
        "RefundComplete",
        "RefundRelease",
    ];
}

public enum FinanceAdministrationQueryOutcome
{
    Succeeded,
    ValidationFailed,
    NotFound,
}

public sealed record AdminFinancePage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalItems,
    int TotalPages);

public sealed record AdminPaymentListItem(
    Guid PaymentId,
    string OrderCode,
    Guid UserId,
    string UserDisplayName,
    string? UserEmail,
    string OrderType,
    long AmountVnd,
    long? PaidCoinsSnapshot,
    string OrderStatus,
    string ProviderPaymentStatus,
    string FulfillmentStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? VerifiedPaidAt,
    string? ReconciliationReason);

public sealed record AdminProviderTransactionItem(
    Guid TransactionId,
    string Provider,
    string ProviderTransactionId,
    long AmountVnd,
    string Direction,
    string AccountNumberLast4,
    string? ReferenceCode,
    DateTimeOffset OccurredAt,
    DateTimeOffset VerifiedAt,
    string ProcessingStatus,
    string? ReconciliationReason);

public sealed record AdminPaymentDetail(
    AdminPaymentListItem Payment,
    IReadOnlyList<AdminProviderTransactionItem> ProviderTransactions);

public sealed record AdminLedgerItem(
    Guid EntryId,
    Guid OperationId,
    Guid UserId,
    string UserDisplayName,
    string? UserEmail,
    string OperationType,
    string SourceType,
    Guid SourceId,
    string CoinType,
    string Bucket,
    long DeltaCoins,
    int LegOrder,
    DateTimeOffset CreatedAt);

public sealed record AdminRefundListItem(
    Guid RefundId,
    Guid PaymentOrderId,
    string OrderCode,
    Guid UserId,
    string UserDisplayName,
    string? UserEmail,
    string Status,
    long OriginalAmountVnd,
    long FeeVnd,
    long NetRefundVnd,
    long RequiredRecoveryCoins,
    DateTimeOffset RequestedAt,
    long Version);

public sealed record AdminRefundRecipientItem(
    string BankCode,
    string AccountNumberMasked,
    string AccountHolderMasked);

public sealed record AdminPaidCoinLotItem(
    Guid LotId,
    long GrantedCoins,
    long SpentCoins,
    long RefundedCoins,
    long ReservedCoins,
    long AvailableCoins,
    long Version);

public sealed record AdminRefundReservationItem(
    Guid ReservationId,
    Guid WalletOperationId,
    long AmountCoins,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record AdminRefundTransferItem(
    Guid TransferRecordId,
    string BankReference,
    long AmountVnd,
    string EvidenceReference,
    string Status,
    DateTimeOffset RecordedAt,
    DateTimeOffset? VerifiedAt,
    string? ConfirmationNote,
    Guid? ResolutionOperationId);

public sealed record AdminRefundAuditItem(
    Guid AuditId,
    Guid ActorId,
    string Action,
    DateTimeOffset OccurredAt,
    string RedactedChanges);

public sealed record AdminRefundDetail(
    AdminRefundListItem Refund,
    string Reason,
    AdminRefundRecipientItem Recipient,
    AdminPaidCoinLotItem? OriginalPaidCoinLot,
    AdminRefundReservationItem? Reservation,
    AdminRefundTransferItem? Transfer,
    IReadOnlyList<AdminRefundAuditItem> AuditTimeline);

public sealed record AdminFinanceListRequest(
    string? Search,
    string? Status,
    string? Type,
    int Page,
    int PageSize);

public interface IFinanceAdministrationQueryStore
{
    Task<AdminFinancePage<AdminPaymentListItem>> ListPaymentsAsync(
        AdminFinanceListRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminPaymentDetail?> GetPaymentAsync(
        Guid paymentId,
        CancellationToken cancellationToken = default);

    Task<AdminFinancePage<AdminLedgerItem>> ListLedgerAsync(
        AdminFinanceListRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminFinancePage<AdminRefundListItem>> ListRefundsAsync(
        AdminFinanceListRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminRefundDetail?> GetRefundAsync(
        Guid refundId,
        CancellationToken cancellationToken = default);
}

public sealed record FinanceAdministrationQueryResult<T>(
    FinanceAdministrationQueryOutcome Outcome,
    T? Value = default);

public sealed class FinanceAdministrationQueryHandler(IFinanceAdministrationQueryStore store)
{
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 200;

    private static readonly HashSet<string> PaymentStatuses =
        new(AdminFinanceContractValues.PaymentOrderStatuses, StringComparer.Ordinal);
    private static readonly HashSet<string> PaymentTypes =
        new(AdminFinanceContractValues.PaymentOrderTypes, StringComparer.Ordinal);
    private static readonly HashSet<string> RefundStatuses =
        new(AdminFinanceContractValues.RefundStatuses, StringComparer.Ordinal);
    private static readonly HashSet<string> LedgerOperationTypes =
        new(AdminFinanceContractValues.WalletOperationTypes, StringComparer.Ordinal);

    public Task<FinanceAdministrationQueryResult<AdminFinancePage<AdminPaymentListItem>>>
        ListPaymentsAsync(string? search, string? status, string? type, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
        ListAsync(search, status, type, page, pageSize, PaymentStatuses, PaymentTypes,
            store.ListPaymentsAsync, cancellationToken);

    public async Task<FinanceAdministrationQueryResult<AdminPaymentDetail>> GetPaymentAsync(
        Guid paymentId,
        CancellationToken cancellationToken = default)
    {
        if (paymentId == Guid.Empty)
            return new(FinanceAdministrationQueryOutcome.ValidationFailed);
        var value = await store.GetPaymentAsync(paymentId, cancellationToken);
        return value is null
            ? new(FinanceAdministrationQueryOutcome.NotFound)
            : new(FinanceAdministrationQueryOutcome.Succeeded, value);
    }

    public Task<FinanceAdministrationQueryResult<AdminFinancePage<AdminLedgerItem>>>
        ListLedgerAsync(string? search, string? operationType, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
        ListAsync(search, operationType, null, page, pageSize, LedgerOperationTypes, null,
            store.ListLedgerAsync, cancellationToken);

    public Task<FinanceAdministrationQueryResult<AdminFinancePage<AdminRefundListItem>>>
        ListRefundsAsync(string? search, string? status, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
        ListAsync(search, status, null, page, pageSize, RefundStatuses, null,
            store.ListRefundsAsync, cancellationToken);

    public async Task<FinanceAdministrationQueryResult<AdminRefundDetail>> GetRefundAsync(
        Guid refundId,
        CancellationToken cancellationToken = default)
    {
        if (refundId == Guid.Empty)
            return new(FinanceAdministrationQueryOutcome.ValidationFailed);
        var value = await store.GetRefundAsync(refundId, cancellationToken);
        return value is null
            ? new(FinanceAdministrationQueryOutcome.NotFound)
            : new(FinanceAdministrationQueryOutcome.Succeeded, value);
    }

    private static async Task<FinanceAdministrationQueryResult<AdminFinancePage<T>>> ListAsync<T>(
        string? search,
        string? status,
        string? type,
        int page,
        int pageSize,
        HashSet<string>? allowedStatuses,
        HashSet<string>? allowedTypes,
        Func<AdminFinanceListRequest, CancellationToken, Task<AdminFinancePage<T>>> query,
        CancellationToken cancellationToken)
    {
        var normalizedSearch = search?.Trim();
        if (page < 1 || pageSize is < 1 or > MaximumPageSize ||
            normalizedSearch?.Length > MaximumSearchLength ||
            !IsAllowed(status, allowedStatuses) || !IsAllowed(type, allowedTypes))
            return new(FinanceAdministrationQueryOutcome.ValidationFailed);

        var result = await query(new AdminFinanceListRequest(
            string.IsNullOrEmpty(normalizedSearch) ? null : normalizedSearch,
            string.IsNullOrEmpty(status) ? null : status,
            string.IsNullOrEmpty(type) ? null : type,
            page,
            pageSize), cancellationToken);
        return new(FinanceAdministrationQueryOutcome.Succeeded, result);
    }

    private static bool IsAllowed(string? value, HashSet<string>? allowed) =>
        string.IsNullOrEmpty(value) || allowed is null || allowed.Contains(value);
}
