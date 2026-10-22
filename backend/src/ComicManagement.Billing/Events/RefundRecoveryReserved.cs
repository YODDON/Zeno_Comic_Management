namespace ComicManagement.Billing.Events;

public static partial class BillingEventTypes
{
    public const string RefundRecoveryReserved = "Billing.RefundRecoveryReserved.v1";
}

public sealed record RefundRecoveryReservedEvent(
    Guid RefundRequestId,
    Guid UserId,
    Guid PaymentOrderId,
    Guid PaidCoinLotId,
    Guid WalletOperationId,
    long ReservedCoins,
    DateTimeOffset ReservedAt);
