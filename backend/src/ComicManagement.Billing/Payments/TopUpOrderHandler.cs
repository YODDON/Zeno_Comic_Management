using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

public sealed record CreateTopUpOrderStoreRequest(
    Guid OrderId,
    Guid UserId,
    long AmountVnd,
    long PaidCoins,
    string IdempotencyKey,
    string RequestFingerprint,
    TimeSpan TimeToLive);

public interface ITopUpOrderStore
{
    Task<TopUpOrderStoreResult> CreateOrReplayAsync(
        CreateTopUpOrderStoreRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class TopUpOrderHandler(
    ITopUpOrderStore store,
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

        var orderId = Guid.NewGuid();
        var fingerprint = Fingerprint(amountVnd);
        var stored = await store.CreateOrReplayAsync(
            new CreateTopUpOrderStoreRequest(
                orderId,
                userId,
                amountVnd,
                TopUpAmountPolicy.CalculatePaidCoins(amountVnd),
                idempotencyKey,
                fingerprint,
                policy.TimeToLive),
            cancellationToken);
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
                authoritative.OrderStatus,
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
}
