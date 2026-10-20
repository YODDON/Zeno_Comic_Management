using System.Globalization;
using ComicManagement.Billing.Payments;

namespace ComicManagement.Billing.Refunds;

public enum RefundRequestOutcome
{
    Succeeded,
    ValidationFailed,
    NotFound,
    OrderNotEligible,
    WindowExpired,
    RecoveryInsufficient,
    RefundAlreadyExists,
    IdempotencyConflict,
}

public sealed record RefundRecipientInput(
    string BankCode,
    string AccountNumber,
    string AccountHolder);

public sealed record ProtectedRefundRecipient(
    byte[] AccountNumberCiphertext,
    byte[] AccountHolderCiphertext,
    string EncryptionKeyVersion);

public sealed record UnprotectedRefundRecipient(
    string AccountNumber,
    string AccountHolder);

public interface IRefundRecipientProtector
{
    ProtectedRefundRecipient Protect(string accountNumber, string accountHolder);

    UnprotectedRefundRecipient Unprotect(
        byte[] accountNumberCiphertext,
        byte[] accountHolderCiphertext,
        string protectionVersion);
}

public sealed record RefundRequestFingerprints(
    string CurrentVersion,
    string CurrentFingerprint,
    IReadOnlyDictionary<string, string> CandidatesByVersion);

public interface IRefundRequestFingerprintService
{
    RefundRequestFingerprints CreateFingerprints(string canonicalRequest);
}

public sealed record RefundEligibilityState(
    Guid OrderId,
    long OriginalAmountVnd,
    long FeeVnd,
    long NetRefundVnd,
    long LotGrantedCoins,
    long LotAvailableCoins,
    bool Eligible,
    string? ReasonCode,
    DateTimeOffset PaidAt);

public sealed record RefundRequestState(
    Guid RefundId,
    Guid PaymentOrderId,
    string Status,
    string Reason,
    long OriginalAmountVnd,
    long FeeVnd,
    long NetRefundVnd,
    long RequiredRecoveryCoins,
    DateTimeOffset RequestedAt,
    string BankCode,
    string AccountNumberMasked,
    string AccountHolderMasked);

public sealed record RefundRequestResult(
    RefundRequestOutcome Outcome,
    RefundRequestState? State = null);

public sealed record RefundEligibilityResult(
    RefundRequestOutcome Outcome,
    RefundEligibilityState? State = null);

public sealed record RefundEligibilitySnapshot(
    Guid OrderId,
    string OrderType,
    string OrderStatus,
    string ProviderPaymentStatus,
    string FulfillmentStatus,
    long OriginalAmountVnd,
    long? PaidCoinsSnapshot,
    DateTimeOffset? VerifiedPaidAt,
    long? LotGrantedCoins,
    long? LotSpentCoins,
    long? LotRefundedCoins,
    long? LotReservedCoins,
    bool RefundAlreadyExists);

public static class RefundEligibilityPolicy
{
    public static RefundEligibilityResult Evaluate(
        RefundEligibilitySnapshot snapshot,
        DateTimeOffset serverNow)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (serverNow.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Server time must use the UTC offset.", nameof(serverNow));
        }

        if (snapshot.OrderType != PaymentOrderTypes.TopUp ||
            snapshot.OrderStatus != TopUpOrderStatuses.Succeeded ||
            snapshot.ProviderPaymentStatus != ProviderPaymentStatuses.Verified ||
            snapshot.FulfillmentStatus != PaymentFulfillmentStatuses.Fulfilled ||
            snapshot.VerifiedPaidAt is not { } paidAt ||
            snapshot.PaidCoinsSnapshot is not { } paidCoins ||
            snapshot.LotGrantedCoins is not { } grantedCoins ||
            snapshot.LotSpentCoins is not { } spentCoins ||
            snapshot.LotRefundedCoins is not { } refundedCoins ||
            snapshot.LotReservedCoins is not { } reservedCoins ||
            grantedCoins != paidCoins)
        {
            return new RefundEligibilityResult(RefundRequestOutcome.OrderNotEligible);
        }

        var availableCoins = checked(grantedCoins - spentCoins - refundedCoins - reservedCoins);
        var fee = checked(snapshot.OriginalAmountVnd * RefundRequest.FeePercent / 100);
        var outcome = snapshot.RefundAlreadyExists
            ? RefundRequestOutcome.RefundAlreadyExists
            : serverNow > paidAt.AddDays(RefundRequest.RequestWindowDays)
                ? RefundRequestOutcome.WindowExpired
                : availableCoins != grantedCoins ||
                  spentCoins != 0 || refundedCoins != 0 || reservedCoins != 0
                    ? RefundRequestOutcome.RecoveryInsufficient
                    : RefundRequestOutcome.Succeeded;
        var reasonCode = outcome switch
        {
            RefundRequestOutcome.RefundAlreadyExists => "REFUND_ALREADY_EXISTS",
            RefundRequestOutcome.WindowExpired => "REFUND_WINDOW_EXPIRED",
            RefundRequestOutcome.RecoveryInsufficient => "REFUND_RECOVERY_INSUFFICIENT",
            _ => null,
        };
        return new RefundEligibilityResult(
            outcome,
            new RefundEligibilityState(
                snapshot.OrderId,
                snapshot.OriginalAmountVnd,
                fee,
                checked(snapshot.OriginalAmountVnd - fee),
                grantedCoins,
                availableCoins,
                outcome == RefundRequestOutcome.Succeeded,
                reasonCode,
                paidAt));
    }
}

public sealed record CreateRefundStoreRequest(
    Guid RefundId,
    Guid RecipientId,
    Guid UserId,
    Guid PaymentOrderId,
    string Reason,
    string IdempotencyKey,
    RefundRequestFingerprints Fingerprints,
    string BankCode,
    ProtectedRefundRecipient ProtectedRecipient,
    string AccountNumberLast4,
    string AccountNumberMasked,
    string AccountHolderMasked);

public interface IRefundRequestStore
{
    Task<RefundEligibilityResult> GetEligibilityAsync(
        Guid userId,
        Guid paymentOrderId,
        CancellationToken cancellationToken = default);

    Task<RefundRequestResult> CreateOrReplayAsync(
        CreateRefundStoreRequest request,
        CancellationToken cancellationToken = default);

    Task<RefundRequestState?> GetAsync(
        Guid userId,
        Guid refundId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RefundRequestState>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed class RefundRequestHandler(
    IRefundRequestStore store,
    IRefundRecipientProtector recipientProtector,
    IRefundRequestFingerprintService fingerprintService)
{
    public Task<RefundEligibilityResult> GetEligibilityAsync(
        Guid userId,
        Guid paymentOrderId,
        CancellationToken cancellationToken = default) =>
        userId == Guid.Empty || paymentOrderId == Guid.Empty
            ? Task.FromResult(new RefundEligibilityResult(
                RefundRequestOutcome.ValidationFailed))
            : store.GetEligibilityAsync(userId, paymentOrderId, cancellationToken);

    public async Task<RefundRequestResult> CreateAsync(
        Guid userId,
        Guid paymentOrderId,
        string? reason,
        RefundRecipientInput? recipient,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(
                userId,
                paymentOrderId,
                reason,
                recipient,
                idempotencyKey,
                out var normalized))
        {
            return new RefundRequestResult(RefundRequestOutcome.ValidationFailed);
        }

        var protectedRecipient = recipientProtector.Protect(
            normalized.AccountNumber,
            normalized.AccountHolder);
        var canonicalRequest = Canonicalize(paymentOrderId, normalized);
        return await store.CreateOrReplayAsync(
            new CreateRefundStoreRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                userId,
                paymentOrderId,
                normalized.Reason,
                normalized.IdempotencyKey,
                fingerprintService.CreateFingerprints(canonicalRequest),
                normalized.BankCode,
                protectedRecipient,
                normalized.AccountNumber[^Math.Min(4, normalized.AccountNumber.Length)..],
                MaskAccountNumber(normalized.AccountNumber),
                MaskAccountHolder(normalized.AccountHolder)),
            cancellationToken);
    }

    public Task<RefundRequestState?> GetAsync(
        Guid userId,
        Guid refundId,
        CancellationToken cancellationToken = default) =>
        userId == Guid.Empty || refundId == Guid.Empty
            ? Task.FromResult<RefundRequestState?>(null)
            : store.GetAsync(userId, refundId, cancellationToken);

    public Task<IReadOnlyList<RefundRequestState>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        userId == Guid.Empty
            ? Task.FromResult<IReadOnlyList<RefundRequestState>>([])
            : store.ListAsync(userId, cancellationToken);

    private static bool TryNormalize(
        Guid userId,
        Guid paymentOrderId,
        string? reason,
        RefundRecipientInput? recipient,
        string? idempotencyKey,
        out NormalizedRefundRequest normalized)
    {
        normalized = default!;
        if (userId == Guid.Empty || paymentOrderId == Guid.Empty || recipient is null ||
            string.IsNullOrWhiteSpace(reason) ||
            string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return false;
        }

        var normalizedReason = reason.Trim();
        var normalizedKey = idempotencyKey.Trim();
        var bankCode = recipient.BankCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var accountNumber = recipient.AccountNumber?.Trim() ?? string.Empty;
        var accountHolder = CollapseWhitespace(recipient.AccountHolder).ToUpperInvariant();
        if (normalizedReason.Length > RefundRequest.MaximumReasonLength ||
            normalizedKey != idempotencyKey ||
            normalizedKey.Length > RefundRequest.MaximumIdempotencyKeyLength ||
            bankCode.Length is < 2 or > RefundRecipient.MaximumBankCodeLength ||
            !bankCode.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') ||
            accountNumber.Length is < 4 or > RefundRecipient.MaximumAccountNumberLength ||
            !accountNumber.All(char.IsAsciiDigit) ||
            accountHolder.Length is < 2 or > RefundRecipient.MaximumAccountHolderLength)
        {
            return false;
        }

        normalized = new NormalizedRefundRequest(
            normalizedReason,
            normalizedKey,
            bankCode,
            accountNumber,
            accountHolder);
        return true;
    }

    internal static string Canonicalize(
        Guid paymentOrderId,
        NormalizedRefundRequest request)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Refund|order={paymentOrderId:N}|reason={request.Reason.Length}:{request.Reason}|bank={request.BankCode.Length}:{request.BankCode}|account={request.AccountNumber.Length}:{request.AccountNumber}|holder={request.AccountHolder.Length}:{request.AccountHolder}");
    }

    private static string CollapseWhitespace(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string MaskAccountNumber(string accountNumber) =>
        string.Concat(new string('*', Math.Max(0, accountNumber.Length - 4)),
            accountNumber[^Math.Min(4, accountNumber.Length)..]);

    private static string MaskAccountHolder(string accountHolder) =>
        string.Join(' ', accountHolder.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => string.Concat(part[0], "***")));

    internal sealed record NormalizedRefundRequest(
        string Reason,
        string IdempotencyKey,
        string BankCode,
        string AccountNumber,
        string AccountHolder);
}
