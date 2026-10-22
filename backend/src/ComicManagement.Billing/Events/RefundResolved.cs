namespace ComicManagement.Billing.Events;

public static partial class BillingEventTypes
{
    public const string RefundCompleted = "Billing.RefundCompleted.v1";
    public const string RefundReservationReleased = "Billing.RefundReservationReleased.v1";
}

public sealed record RefundCompletedEvent(
    Guid RefundRequestId,
    Guid UserId,
    Guid PaymentOrderId,
    Guid PaidCoinLotId,
    Guid TransferRecordId,
    Guid WalletOperationId,
    long RefundedCoins,
    long NetRefundVnd,
    DateTimeOffset CompletedAt);

public sealed record RefundReservationReleasedEvent(
    Guid RefundRequestId,
    Guid UserId,
    Guid PaymentOrderId,
    Guid PaidCoinLotId,
    Guid TransferRecordId,
    Guid WalletOperationId,
    long ReleasedCoins,
    DateTimeOffset ReleasedAt);
