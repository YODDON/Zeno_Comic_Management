using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComicManagement.Billing.Payments;

public sealed class SePayWebhookOptions
{
    public const string SectionName = "Billing:SePayWebhook";

    public bool Enabled { get; set; }

    public string Secret { get; set; } = string.Empty;

    public string ExpectedAccountNumber { get; set; } = string.Empty;

    public int TimestampToleranceSeconds { get; set; } = 300;

    public int MaximumBodyBytes { get; set; } = 64 * 1024;

    public int PermitLimitPerMinute { get; set; } = 120;
}

public sealed class SePayWebhookPolicy
{
    public SePayWebhookPolicy(
        bool enabled,
        string secret,
        string expectedAccountNumber,
        TimeSpan timestampTolerance,
        int maximumBodyBytes)
    {
        if (timestampTolerance <= TimeSpan.Zero || timestampTolerance > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampTolerance),
                "The SePay timestamp tolerance must be between one second and fifteen minutes.");
        }

        if (maximumBodyBytes is < 1024 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBodyBytes),
                "The SePay webhook body limit must be between 1 KiB and 1 MiB.");
        }

        if (enabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(secret);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedAccountNumber);
            if (secret.Length is < 32 or > 500)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(secret),
                    "The SePay HMAC secret must contain between 32 and 500 characters.");
            }

            if (expectedAccountNumber.Length > SePayNormalizedTransaction.MaximumAccountNumberLength)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedAccountNumber));
            }
        }

        Enabled = enabled;
        Secret = secret;
        ExpectedAccountNumber = expectedAccountNumber;
        TimestampTolerance = timestampTolerance;
        MaximumBodyBytes = maximumBodyBytes;
    }

    public bool Enabled { get; }

    internal string Secret { get; }

    internal string ExpectedAccountNumber { get; }

    public TimeSpan TimestampTolerance { get; }

    public int MaximumBodyBytes { get; }
}

public static class SePayWebhookHeaders
{
    public const string Signature = "X-SePay-Signature";

    public const string Timestamp = "X-SePay-Timestamp";
}

public static class SePayReconciliationReasons
{
    public const string DirectionNotIncoming = "DIRECTION_NOT_INCOMING";

    public const string UnexpectedAccount = "UNEXPECTED_ACCOUNT";

    public const string PaymentCodeMissing = "PAYMENT_CODE_MISSING";

    public const string ProviderTransactionConflict = "PROVIDER_TRANSACTION_CONFLICT";

    public const string OrderNotFound = "ORDER_NOT_FOUND";

    public const string UnsupportedOrderType = "UNSUPPORTED_ORDER_TYPE";

    public const string AmountMismatch = "AMOUNT_MISMATCH";

    public const string LatePayment = "LatePayment";

    public const string OrderAlreadyFulfilled = "ORDER_ALREADY_FULFILLED";

    public const string FulfillmentStateConflict = "FULFILLMENT_STATE_CONFLICT";
}

public enum SePayWebhookVerificationOutcome
{
    Accepted,
    Disabled,
    Unauthorized,
    InvalidPayload,
}

public sealed record SePayWebhookRequest(
    byte[] RawBody,
    string? Signature,
    string? Timestamp);

public sealed record SePayNormalizedTransaction(
    string ProviderTransactionId,
    string PayloadFingerprint,
    long AmountVnd,
    string Direction,
    string? PaymentCode,
    string AccountNumberLast4,
    string? ReferenceCode,
    DateTimeOffset OccurredAt,
    string? ReconciliationReason)
{
    public const int MaximumProviderTransactionIdLength = 128;
    public const int MaximumAccountNumberLength = 64;
    public const int MaximumPaymentCodeLength = 64;
    public const int MaximumReferenceCodeLength = 128;
}

public sealed record SePayWebhookVerificationResult(
    SePayWebhookVerificationOutcome Outcome,
    SePayNormalizedTransaction? Transaction = null);

public sealed class SePayWebhookAdapter(SePayWebhookPolicy policy)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public SePayWebhookVerificationResult VerifyAndNormalize(
        SePayWebhookRequest request,
        DateTimeOffset serverNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUtc(serverNow, nameof(serverNow));

        if (!policy.Enabled)
        {
            return new SePayWebhookVerificationResult(
                SePayWebhookVerificationOutcome.Disabled);
        }

        if (request.RawBody.Length is 0 || request.RawBody.Length > policy.MaximumBodyBytes)
        {
            return new SePayWebhookVerificationResult(
                SePayWebhookVerificationOutcome.InvalidPayload);
        }

        if (!TryVerifySignature(request, serverNow))
        {
            return new SePayWebhookVerificationResult(
                SePayWebhookVerificationOutcome.Unauthorized);
        }

        return TryNormalize(request.RawBody, out var transaction)
            ? new SePayWebhookVerificationResult(
                SePayWebhookVerificationOutcome.Accepted,
                transaction)
            : new SePayWebhookVerificationResult(
                SePayWebhookVerificationOutcome.InvalidPayload);
    }

    private bool TryVerifySignature(
        SePayWebhookRequest request,
        DateTimeOffset serverNow)
    {
        if (string.IsNullOrWhiteSpace(request.Timestamp) ||
            !long.TryParse(
                request.Timestamp,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var timestamp) ||
            !string.Equals(
                request.Timestamp,
                timestamp.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return false;
        }

        var nowSeconds = serverNow.ToUnixTimeSeconds();
        var toleranceSeconds = checked((long)policy.TimestampTolerance.TotalSeconds);
        if (timestamp < nowSeconds - toleranceSeconds ||
            timestamp > nowSeconds + toleranceSeconds ||
            !TryReadSignature(request.Signature, out var suppliedSignature))
        {
            return false;
        }

        var timestampBytes = Encoding.ASCII.GetBytes(request.Timestamp);
        var signedBytes = new byte[timestampBytes.Length + 1 + request.RawBody.Length];
        timestampBytes.CopyTo(signedBytes, 0);
        signedBytes[timestampBytes.Length] = (byte)'.';
        request.RawBody.CopyTo(signedBytes, timestampBytes.Length + 1);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(policy.Secret));
        var expectedSignature = hmac.ComputeHash(signedBytes);
        return CryptographicOperations.FixedTimeEquals(
            expectedSignature,
            suppliedSignature);
    }

    private bool TryNormalize(
        byte[] rawBody,
        out SePayNormalizedTransaction? transaction)
    {
        transaction = null;
        SePayPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SePayPayload>(rawBody, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null ||
            payload.Id <= 0 ||
            payload.TransferAmount <= 0 ||
            !ValidRequiredText(payload.Gateway, 64) ||
            !ValidRequiredText(
                payload.AccountNumber,
                SePayNormalizedTransaction.MaximumAccountNumberLength) ||
            !ValidRequiredText(payload.Content, 1024) ||
            payload.Code?.Length > SePayNormalizedTransaction.MaximumPaymentCodeLength ||
            payload.ReferenceCode?.Length > SePayNormalizedTransaction.MaximumReferenceCodeLength ||
            !TryNormalizeDirection(payload.TransferType, out var direction) ||
            !DateTime.TryParseExact(
                payload.TransactionDate,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTransactionTime))
        {
            return false;
        }

        var occurredAt = new DateTimeOffset(
                DateTime.SpecifyKind(localTransactionTime, DateTimeKind.Unspecified),
                TimeSpan.FromHours(7))
            .ToUniversalTime();
        var paymentCode = string.IsNullOrWhiteSpace(payload.Code)
            ? null
            : payload.Code;
        var referenceCode = string.IsNullOrWhiteSpace(payload.ReferenceCode)
            ? null
            : payload.ReferenceCode;
        var accountNumber = payload.AccountNumber!;
        var reconciliationReason = direction != ProviderTransactionDirections.Incoming
            ? SePayReconciliationReasons.DirectionNotIncoming
            : !FixedTimeTextEquals(accountNumber, policy.ExpectedAccountNumber)
                ? SePayReconciliationReasons.UnexpectedAccount
                : paymentCode is null
                    ? SePayReconciliationReasons.PaymentCodeMissing
                    : null;

        transaction = new SePayNormalizedTransaction(
            payload.Id.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant(),
            payload.TransferAmount,
            direction,
            paymentCode,
            LastFour(accountNumber),
            referenceCode,
            occurredAt,
            reconciliationReason);
        return true;
    }

    private static bool TryReadSignature(
        string? value,
        out byte[] signature)
    {
        signature = [];
        const string prefix = "sha256=";
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + 64)
        {
            return false;
        }

        try
        {
            signature = Convert.FromHexString(value[prefix.Length..]);
            return signature.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryNormalizeDirection(
        string? value,
        out string direction)
    {
        direction = value switch
        {
            "in" => ProviderTransactionDirections.Incoming,
            "out" => ProviderTransactionDirections.Outgoing,
            _ => string.Empty,
        };
        return direction.Length > 0;
    }

    private static bool ValidRequiredText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static bool FixedTimeTextEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string LastFour(string value) =>
        value.Length <= 4 ? value : value[^4..];

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }

    private sealed class SePayPayload
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("gateway")]
        public string? Gateway { get; init; }

        [JsonPropertyName("transactionDate")]
        public string? TransactionDate { get; init; }

        [JsonPropertyName("accountNumber")]
        public string? AccountNumber { get; init; }

        [JsonPropertyName("code")]
        public string? Code { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("transferType")]
        public string? TransferType { get; init; }

        [JsonPropertyName("transferAmount")]
        public long TransferAmount { get; init; }

        [JsonPropertyName("referenceCode")]
        public string? ReferenceCode { get; init; }
    }
}

public enum SePayWebhookStoreOutcome
{
    Accepted,
    Duplicate,
    ReconciliationRequired,
    Fulfilled,
}

public sealed record SePayWebhookStoreResult(
    SePayWebhookStoreOutcome Outcome,
    Guid ProviderTransactionId,
    bool IsReplay = false);

public interface ISePayWebhookStore
{
    Task<SePayWebhookStoreResult> IngestAsync(
        SePayNormalizedTransaction transaction,
        CancellationToken cancellationToken = default);
}

public enum SePayWebhookOutcome
{
    Accepted,
    Disabled,
    Unauthorized,
    InvalidPayload,
}

public sealed record SePayWebhookResult(
    SePayWebhookOutcome Outcome,
    SePayWebhookStoreOutcome? StoreOutcome = null);

public sealed class SePayWebhookHandler(
    SePayWebhookAdapter adapter,
    ISePayWebhookStore store)
{
    public async Task<SePayWebhookResult> HandleAsync(
        SePayWebhookRequest request,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken = default)
    {
        var verification = adapter.VerifyAndNormalize(request, serverNow);
        if (verification.Outcome != SePayWebhookVerificationOutcome.Accepted ||
            verification.Transaction is null)
        {
            return new SePayWebhookResult(verification.Outcome switch
            {
                SePayWebhookVerificationOutcome.Disabled => SePayWebhookOutcome.Disabled,
                SePayWebhookVerificationOutcome.Unauthorized => SePayWebhookOutcome.Unauthorized,
                _ => SePayWebhookOutcome.InvalidPayload,
            });
        }

        var stored = await store.IngestAsync(verification.Transaction, cancellationToken);
        return new SePayWebhookResult(SePayWebhookOutcome.Accepted, stored.Outcome);
    }
}
