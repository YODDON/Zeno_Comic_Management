namespace ComicManagement.Billing.Events;

public static class BillingEventTypes
{
    public const string WalletTopUpCompleted = "Billing.WalletTopUpCompleted.v1";
}

public sealed record WalletTopUpCompletedEvent(
    Guid PaymentOrderId,
    Guid ProviderTransactionId,
    Guid UserId,
    long AmountVnd,
    long PaidCoins,
    DateTimeOffset FulfilledAt);
