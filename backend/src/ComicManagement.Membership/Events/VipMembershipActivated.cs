namespace ComicManagement.Membership.Events;

public static class MembershipEventTypes
{
    public const string VipMembershipActivated = "Membership.VipMembershipActivated.v1";
}

public sealed record VipMembershipActivatedEvent(
    Guid PaymentOrderId,
    Guid ProviderTransactionId,
    Guid VipTransactionId,
    Guid UserId,
    Guid PlanId,
    long AmountVnd,
    int DurationHours,
    int DiscountPercent,
    DateTimeOffset EffectiveStart,
    DateTimeOffset ExpiresAt,
    DateTimeOffset FulfilledAt);
