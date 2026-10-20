using ComicManagement.Billing.Payments;

namespace ComicManagement.Billing.Refunds;

public enum RefundFinancialLoadOutcome
{
    Succeeded,
    NotFound,
    IntegrityConflict,
}

public sealed record RefundFinancialProcessingState(
    RefundRequestState Request,
    Guid UserId,
    ProtectedRefundRecipient Recipient,
    Guid OriginalPaidCoinLotId,
    string OrderType,
    string OrderStatus,
    string ProviderPaymentStatus,
    string FulfillmentStatus,
    long OrderAmountVnd,
    long PaidCoinsSnapshot,
    long LotGrantedCoins,
    long LotSpentCoins,
    long LotRefundedCoins,
    long LotReservedCoins)
{
    public long LotAvailableCoins => checked(
        LotGrantedCoins - LotSpentCoins - LotRefundedCoins - LotReservedCoins);

    public bool IsFullyRecoverable =>
        LotAvailableCoins == LotGrantedCoins &&
        LotSpentCoins == 0 &&
        LotRefundedCoins == 0 &&
        LotReservedCoins == 0;
}

public sealed record RefundFinancialLoadResult(
    RefundFinancialLoadOutcome Outcome,
    RefundFinancialProcessingState? State = null);

public interface IRefundFinancialProcessingLoader
{
    Task<RefundFinancialLoadResult> LoadForFinancialProcessingAsync(
        Guid refundRequestId,
        CancellationToken cancellationToken = default);
}
