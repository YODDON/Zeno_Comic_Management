using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ComicManagement.Membership.Memberships;

public sealed class VipOrderOptions
{
    public const string SectionName = "Membership";

    public int VipOrderTtlMinutes { get; set; } = 30;
}

public sealed class VipOrderPolicy
{
    public VipOrderPolicy(TimeSpan timeToLive)
    {
        if (timeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive),
                "VIP order TTL must be positive.");
        }

        TimeToLive = timeToLive;
    }

    public TimeSpan TimeToLive { get; }
}

public enum VipMembershipOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
    IdempotencyConflict = 3,
    ConcurrencyConflict = 4,
}

public sealed record VipPlanState(
    Guid PlanId,
    long PriceVnd,
    int DurationHours,
    int DiscountPercent,
    long Version);

public sealed record VipMembershipState(
    bool IsActive,
    DateTimeOffset? ExpiresAt);

public sealed record VipCheckoutState(
    Guid OrderId,
    long AmountVnd,
    int DurationHours,
    string Status,
    DateTimeOffset ExpiresAt);

public sealed record VipMembershipResult(
    VipMembershipOutcome Outcome,
    VipPlanState? Plan = null,
    VipMembershipState? Membership = null,
    VipCheckoutState? Checkout = null);

public sealed record CreateVipCheckoutStoreRequest(
    Guid OrderId,
    Guid UserId,
    Guid PlanId,
    string IdempotencyKey,
    string RequestFingerprint,
    TimeSpan TimeToLive);

public sealed record UpdateVipPlanPriceStoreRequest(
    Guid ActorId,
    long PriceVnd,
    long ExpectedVersion,
    string CorrelationId);

public interface IVipMembershipStore
{
    Task<VipMembershipResult> GetPlanAsync(
        CancellationToken cancellationToken = default);

    Task<VipMembershipResult> GetMembershipAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<VipMembershipResult> CreateOrReplayCheckoutAsync(
        CreateVipCheckoutStoreRequest request,
        CancellationToken cancellationToken = default);

    Task<VipMembershipResult> UpdateFutureOrderPriceAsync(
        UpdateVipPlanPriceStoreRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class VipMembershipHandler(
    IVipMembershipStore store,
    VipOrderPolicy orderPolicy)
{
    public Task<VipMembershipResult> GetPlanAsync(
        CancellationToken cancellationToken = default) =>
        store.GetPlanAsync(cancellationToken);

    public Task<VipMembershipResult> GetMembershipAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        userId == Guid.Empty
            ? ValidationFailed()
            : store.GetMembershipAsync(userId, cancellationToken);

    public Task<VipMembershipResult> CheckoutAsync(
        Guid userId,
        Guid planId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            planId == Guid.Empty ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            !string.Equals(idempotencyKey, idempotencyKey.Trim(), StringComparison.Ordinal) ||
            idempotencyKey.Length > 200)
        {
            return ValidationFailed();
        }

        return store.CreateOrReplayCheckoutAsync(
            new CreateVipCheckoutStoreRequest(
                Guid.NewGuid(),
                userId,
                planId,
                idempotencyKey,
                Fingerprint(planId),
                orderPolicy.TimeToLive),
            cancellationToken);
    }

    public Task<VipMembershipResult> UpdateFutureOrderPriceAsync(
        Guid actorId,
        long priceVnd,
        long expectedVersion,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty ||
            priceVnd <= 0 ||
            expectedVersion < 1 ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            return ValidationFailed();
        }

        return store.UpdateFutureOrderPriceAsync(
            new UpdateVipPlanPriceStoreRequest(
                actorId,
                priceVnd,
                expectedVersion,
                correlationId),
            cancellationToken);
    }

    private static string Fingerprint(Guid planId)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"VipPurchase|planId={planId:D}");
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static Task<VipMembershipResult> ValidationFailed() =>
        Task.FromResult(new VipMembershipResult(VipMembershipOutcome.ValidationFailed));
}
