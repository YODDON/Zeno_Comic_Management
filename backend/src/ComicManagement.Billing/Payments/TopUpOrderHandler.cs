using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ComicManagement.Shared.Time;

namespace ComicManagement.Billing.Payments;

public enum TopUpOrderOutcome
{
    Succeeded,
    ValidationFailed,
    IdempotencyConflict,
}

public sealed record TopUpOrderState(
    Guid OrderId,
    string OrderCode,
    long AmountVnd,
    long PaidCoinsExpected,
    string Status,
    DateTimeOffset ExpiresAt);

public sealed record TopUpOrderResult(
    TopUpOrderOutcome Outcome,
    TopUpOrderState? State = null);

public sealed record TopUpOrderStoreResult(
    TopUpOrderOutcome Outcome,
    PaymentOrder? Order = null);

public interface ITopUpOrderStore
{
    Task<TopUpOrderStoreResult> CreateOrReplayAsync(
        PaymentOrder candidate,
        CancellationToken cancellationToken = default);
}

public sealed class TopUpOrderHandler(
    ITopUpOrderStore store,
    IClock clock,
    TopUpOrderPolicy policy)
{
    public async Task<TopUpOrderResult> CreateAsync(
        Guid userId,
        long amountVnd,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            !string.Equals(idempotencyKey, idempotencyKey.Trim(), StringComparison.Ordinal) ||
            idempotencyKey.Length > PaymentOrder.MaximumIdempotencyKeyLength ||
            !TopUpAmountPolicy.IsValid(amountVnd))
        {
            return new TopUpOrderResult(TopUpOrderOutcome.ValidationFailed);
        }

        var createdAt = NormalizeUtc(clock.UtcNow);
        var orderId = Guid.NewGuid();
        var fingerprint = Fingerprint(amountVnd);
        var order = PaymentOrder.CreateTopUp(
            orderId,
            userId,
            $"ZENO-{orderId:N}",
            amountVnd,
            TopUpAmountPolicy.CalculatePaidCoins(amountVnd),
            idempotencyKey,
            fingerprint,
            createdAt,
            NormalizeUtc(createdAt.Add(policy.TimeToLive)));
        var stored = await store.CreateOrReplayAsync(order, cancellationToken);
        if (stored.Outcome != TopUpOrderOutcome.Succeeded || stored.Order is null)
        {
            return new TopUpOrderResult(stored.Outcome);
        }

        var authoritative = stored.Order;
        return new TopUpOrderResult(
            TopUpOrderOutcome.Succeeded,
            new TopUpOrderState(
                authoritative.Id,
                authoritative.OrderCode,
                authoritative.AmountVnd,
                authoritative.PaidCoinsSnapshot ?? throw new InvalidOperationException(
                    "A TopUp order requires its Paid Coins snapshot."),
                authoritative.GetTopUpStatus(createdAt),
                authoritative.ExpiresAt));
    }

    private static string Fingerprint(long amountVnd)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"TopUp|amountVnd={amountVnd}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
