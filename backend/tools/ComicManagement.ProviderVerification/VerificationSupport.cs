using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ComicManagement.ProviderVerification;

public sealed class VerificationConfigurationException(string message) : Exception(message);

public static class VerificationConfiguration
{
    public static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new VerificationConfigurationException(
                $"Missing required environment variable {name}.");
        }

        return value.Trim();
    }

    public static string CloudinaryPrefix(string? value)
    {
        var prefix = string.IsNullOrWhiteSpace(value)
            ? "comic-management/q02-verification"
            : value.Trim().Trim('/');
        if (!prefix.StartsWith("comic-management/q02-verification", StringComparison.Ordinal) ||
            prefix.Contains("..", StringComparison.Ordinal) ||
            prefix.Contains('\\'))
        {
            throw new VerificationConfigurationException(
                "Q02_CLOUDINARY_PREFIX must stay under comic-management/q02-verification.");
        }

        return prefix;
    }

    public static TimeSpan HttpTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromSeconds(20);
        }

        if (!int.TryParse(value, out var seconds) || seconds is < 1 or > 120)
        {
            throw new VerificationConfigurationException(
                "Q02_HTTP_TIMEOUT_SECONDS must be an integer from 1 through 120.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public static int HttpStatus(string name)
    {
        var value = Required(name);
        if (!int.TryParse(value, out var status) || status is < 100 or > 599)
        {
            throw new VerificationConfigurationException($"{name} must be a valid HTTP status code.");
        }

        return status;
    }
}

public sealed record ProviderProbeResult(
    string Outcome,
    int? HttpStatus,
    bool IsExpected,
    string? MediaType);

public static class ProviderProbe
{
    public const string VerifiedDenied = "VerifiedDenied";
    public const string VerifiedAccessible = "VerifiedAccessible";
    public const string UnexpectedAccessible = "UnexpectedAccessible";
    public const string ProviderUnavailable = "ProviderUnavailable/Inconclusive";
    private static readonly HashSet<HttpStatusCode> DenialStatuses =
    [
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound,
    ];

    public static async Task<ProviderProbeResult> ProbeAsync(
        HttpClient client,
        string url,
        bool expectSuccess,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var raster = mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
            if (expectSuccess)
            {
                var successful = response.IsSuccessStatusCode && raster;
                return new ProviderProbeResult(
                    successful ? VerifiedAccessible : ProviderUnavailable,
                    (int)response.StatusCode,
                    successful,
                    mediaType);
            }

            if (DenialStatuses.Contains(response.StatusCode))
            {
                return new ProviderProbeResult(
                    VerifiedDenied,
                    (int)response.StatusCode,
                    true,
                    mediaType);
            }

            return new ProviderProbeResult(
                response.IsSuccessStatusCode ? UnexpectedAccessible : ProviderUnavailable,
                (int)response.StatusCode,
                false,
                mediaType);
        }
        catch (Exception exception) when (IsProviderTransportFailure(exception))
        {
            return new ProviderProbeResult(ProviderUnavailable, null, false, null);
        }
    }

    public static bool IsProviderTransportFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException or SocketException;
}

public static class EvidencePathPolicy
{
    public static string Resolve(string? configuredPath, string? startDirectory = null)
    {
        var repoRoot = FindRepositoryRoot(startDirectory ?? Environment.CurrentDirectory) ??
            FindRepositoryRoot(AppContext.BaseDirectory);
        var candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(
                Path.GetTempPath(),
                "comic-management-q02-evidence",
                $"q02-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json")
            : Path.GetFullPath(configuredPath.Trim());
        var fullPath = Path.GetFullPath(candidate);
        if (repoRoot is not null && IsWithin(fullPath, repoRoot))
        {
            throw new VerificationConfigurationException(
                "Q02 evidence path must be outside the repository.");
        }

        return fullPath;
    }

    public static bool IsWithin(string candidate, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "." ||
            (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             relative != "..");
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ComicManagement.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

public static class CloudinaryVerification
{
    public static bool IsPassed(
        IReadOnlyDictionary<string, bool> checks,
        IReadOnlyDictionary<string, ProviderProbeResult> probes,
        string? providerFailure) =>
        providerFailure is null &&
        checks.Values.All(value => value) &&
        probes.Count == 4 &&
        probes.Values.All(result => result.IsExpected);
}

public static class EvidenceWriter
{
    public static async Task<string> WriteAsync(object evidence)
    {
        var path = EvidencePathPolicy.Resolve(Environment.GetEnvironmentVariable("Q02_EVIDENCE_PATH"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync(json);
        Console.WriteLine($"Redacted evidence written to {path}");
        return path;
    }
}

public enum SePayVerificationScenario
{
    SuccessReplay,
    UnknownOrder,
    AmountMismatch,
    LatePayment,
    InvalidSignature,
    Retry,
}

public static class SePayVerificationScenarioParser
{
    public static SePayVerificationScenario Parse(string value) => value switch
    {
        "success-replay" => SePayVerificationScenario.SuccessReplay,
        "unknown-order" => SePayVerificationScenario.UnknownOrder,
        "amount-mismatch" => SePayVerificationScenario.AmountMismatch,
        "late-payment" => SePayVerificationScenario.LatePayment,
        "invalid-signature" => SePayVerificationScenario.InvalidSignature,
        "retry" => SePayVerificationScenario.Retry,
        _ => throw new VerificationConfigurationException("Unknown SePay verification scenario."),
    };

    public static string ToContractName(SePayVerificationScenario scenario) => scenario switch
    {
        SePayVerificationScenario.SuccessReplay => "success-replay",
        SePayVerificationScenario.UnknownOrder => "unknown-order",
        SePayVerificationScenario.AmountMismatch => "amount-mismatch",
        SePayVerificationScenario.LatePayment => "late-payment",
        SePayVerificationScenario.InvalidSignature => "invalid-signature",
        SePayVerificationScenario.Retry => "retry",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}

public sealed record SePayDatabaseObservation(
    bool ProviderTransactionExists,
    string? ProcessingStatus,
    string? ReconciliationReason,
    bool HasMatchedOrder,
    long DeliveryCount,
    long DuplicateDeliveryCount,
    bool OrderExists,
    string? OrderStatus,
    string? ProviderPaymentStatus,
    string? FulfillmentStatus,
    long PaidLotCount,
    long CreditOperationCount,
    long LedgerPaidCoins,
    long SuccessOutboxCount,
    long ExpectedPaidCoins);

public sealed record SePayVerificationResult(
    string Outcome,
    IReadOnlyDictionary<string, bool> Checks,
    object Observed,
    bool Passed);

public static class SePayDatabaseVerifier
{
    public static async Task<SePayDatabaseObservation> ObserveAsync(
        NpgsqlConnection connection,
        string providerTransactionId,
        string? orderCode,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH selected_order AS (
                SELECT id, order_status, provider_payment_status, fulfillment_status,
                       COALESCE(paid_coins_snapshot, 0) AS expected_paid_coins
                FROM billing.payment_orders
                WHERE order_code = @order_code
            ), selected_transaction AS (
                SELECT id, processing_status, reconciliation_reason, matched_order_id
                FROM billing.provider_transactions
                WHERE provider = 'SePay' AND provider_transaction_id = @provider_transaction_id
            )
            SELECT
                EXISTS(SELECT 1 FROM selected_transaction),
                (SELECT processing_status FROM selected_transaction),
                (SELECT reconciliation_reason FROM selected_transaction),
                COALESCE((SELECT matched_order_id IS NOT NULL FROM selected_transaction), FALSE),
                (SELECT count(*) FROM billing.webhook_deliveries d
                 JOIN selected_transaction t ON t.id = d.provider_transaction_id),
                (SELECT count(*) FROM billing.webhook_deliveries d
                 JOIN selected_transaction t ON t.id = d.provider_transaction_id
                 WHERE d.processing_result = 'Duplicate'),
                EXISTS(SELECT 1 FROM selected_order),
                (SELECT order_status FROM selected_order),
                (SELECT provider_payment_status FROM selected_order),
                (SELECT fulfillment_status FROM selected_order),
                (SELECT count(*) FROM wallet.paid_coin_lots l
                 JOIN selected_order o ON o.id = l.topup_order_id),
                (SELECT count(*) FROM wallet.wallet_operations op
                 JOIN selected_order o ON o.id = op.source_id
                 WHERE op.operation_type = 'TopUpCredit' AND op.source_type = 'PaymentOrder'),
                (SELECT COALESCE(sum(e.delta_coins), 0) FROM wallet.wallet_entries e
                 JOIN wallet.wallet_operations op ON op.id = e.operation_id
                 JOIN selected_order o ON o.id = op.source_id
                 WHERE op.operation_type = 'TopUpCredit' AND e.coin_type = 'Paid'
                   AND e.bucket = 'Available'),
                (SELECT count(*) FROM infrastructure.outbox_messages m
                 JOIN selected_order o ON m.payload ->> 'paymentOrderId' = o.id::text
                 WHERE m.event_type = 'Billing.WalletTopUpCompleted.v1'),
                COALESCE((SELECT expected_paid_coins FROM selected_order), 0)
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("provider_transaction_id", providerTransactionId);
        command.Parameters.AddWithValue("order_code", (object?)orderCode ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("SePay verification observation was not returned.");
        }

        return new SePayDatabaseObservation(
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14));
    }

    public static SePayVerificationResult Verify(
        SePayVerificationScenario scenario,
        SePayDatabaseObservation observation,
        int observedHttpStatus,
        int? initialRetryHttpStatus = null)
    {
        var zeroFinancial = observation.PaidLotCount == 0 &&
            observation.CreditOperationCount == 0 &&
            observation.LedgerPaidCoins == 0 &&
            observation.SuccessOutboxCount == 0;
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["financialSideEffectsMatchScenario"] = scenario is
                SePayVerificationScenario.SuccessReplay or SePayVerificationScenario.Retry
                ? observation.PaidLotCount == 1 &&
                  observation.CreditOperationCount == 1 &&
                  observation.LedgerPaidCoins == observation.ExpectedPaidCoins &&
                  observation.SuccessOutboxCount == 1
                : zeroFinancial,
        };

        switch (scenario)
        {
            case SePayVerificationScenario.SuccessReplay:
            case SePayVerificationScenario.Retry:
                checks["providerFulfilled"] = observation.ProviderTransactionExists &&
                    observation.ProcessingStatus == "Fulfilled" && observation.HasMatchedOrder;
                checks["orderFulfilled"] = observation.OrderExists &&
                    observation.OrderStatus == "Succeeded" &&
                    observation.ProviderPaymentStatus == "Verified" &&
                    observation.FulfillmentStatus == "Fulfilled";
                checks["multipleDeliveriesObserved"] = observation.DeliveryCount >= 2;
                checks["duplicateDeliveryObserved"] = observation.DuplicateDeliveryCount >= 1;
                checks["finalAcknowledgementSucceeded"] = observedHttpStatus is >= 200 and <= 299;
                if (scenario == SePayVerificationScenario.Retry)
                {
                    checks["initialAttemptWasNonSuccess"] = initialRetryHttpStatus is >= 400 and <= 599;
                }
                break;
            case SePayVerificationScenario.UnknownOrder:
                checks["durableReconciliation"] = observation.ProviderTransactionExists &&
                    observation.ProcessingStatus == "ReconciliationRequired" &&
                    observation.ReconciliationReason == "ORDER_NOT_FOUND" &&
                    !observation.HasMatchedOrder && observation.DeliveryCount >= 1;
                checks["providerEventAcknowledged"] = observedHttpStatus is >= 200 and <= 299;
                break;
            case SePayVerificationScenario.AmountMismatch:
                checks["durableReconciliation"] = observation.ProviderTransactionExists &&
                    observation.ProcessingStatus == "ReconciliationRequired" &&
                    observation.ReconciliationReason == "AMOUNT_MISMATCH" &&
                    observation.HasMatchedOrder;
                checks["orderNotFulfilled"] = observation.OrderExists &&
                    observation.OrderStatus == "Pending";
                checks["providerEventAcknowledged"] = observedHttpStatus is >= 200 and <= 299;
                break;
            case SePayVerificationScenario.LatePayment:
                checks["durableReconciliation"] = observation.ProviderTransactionExists &&
                    observation.ProcessingStatus == "ReconciliationRequired" &&
                    observation.ReconciliationReason == "LatePayment" &&
                    observation.HasMatchedOrder;
                checks["orderExpired"] = observation.OrderExists &&
                    observation.OrderStatus == "Expired";
                checks["providerEventAcknowledged"] = observedHttpStatus is >= 200 and <= 299;
                break;
            case SePayVerificationScenario.InvalidSignature:
                checks["rejectedBeforePersistence"] = !observation.ProviderTransactionExists &&
                    observation.DeliveryCount == 0;
                checks["orderNotFulfilled"] = observation.OrderExists &&
                    observation.OrderStatus == "Pending";
                checks["requestRejected"] = observedHttpStatus == 401;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var passed = checks.Values.All(value => value);
        return new SePayVerificationResult(
            passed ? "Verified" : "FailedOrInconclusive",
            checks,
            new
            {
                observation.DeliveryCount,
                observation.DuplicateDeliveryCount,
                observation.PaidLotCount,
                observation.CreditOperationCount,
                observation.LedgerPaidCoins,
                observation.SuccessOutboxCount,
            },
            passed);
    }
}
