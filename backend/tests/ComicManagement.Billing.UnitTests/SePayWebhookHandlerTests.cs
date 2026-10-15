using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ComicManagement.Billing.Payments;
using Xunit;

namespace ComicManagement.Billing.UnitTests;

public sealed class SePayWebhookHandlerTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string ExpectedAccount = "0123456789";
    private static readonly DateTimeOffset ServerNow =
        new(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_hmac_normalizes_the_official_payload_and_ingests_it()
    {
        var store = new StubStore(SePayWebhookStoreOutcome.Accepted);
        var handler = CreateHandler(store);

        var result = await handler.HandleAsync(
            SignedRequest(OfficialPayload()),
            ServerNow);

        Assert.Equal(SePayWebhookOutcome.Accepted, result.Outcome);
        Assert.Equal(SePayWebhookStoreOutcome.Accepted, result.StoreOutcome);
        var transaction = Assert.IsType<SePayNormalizedTransaction>(store.LastTransaction);
        Assert.Equal("92704", transaction.ProviderTransactionId);
        Assert.Equal(5_000_000, transaction.AmountVnd);
        Assert.Equal(ProviderTransactionDirections.Incoming, transaction.Direction);
        Assert.Equal("ZENO-0123456789abcdef", transaction.PaymentCode);
        Assert.Equal("6789", transaction.AccountNumberLast4);
        Assert.Equal("FT24012345678", transaction.ReferenceCode);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 16, 3, 30, 0, TimeSpan.Zero),
            transaction.OccurredAt);
        Assert.Null(transaction.ReconciliationReason);
        Assert.Matches("^[0-9a-f]{64}$", transaction.PayloadFingerprint);
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_before_persistence()
    {
        var store = new StubStore(SePayWebhookStoreOutcome.Accepted);
        var handler = CreateHandler(store);
        var request = SignedRequest(OfficialPayload()) with
        {
            Signature = "sha256=" + new string('0', 64),
        };

        var result = await handler.HandleAsync(request, ServerNow);

        Assert.Equal(SePayWebhookOutcome.Unauthorized, result.Outcome);
        Assert.Null(store.LastTransaction);
    }

    [Fact]
    public async Task Timestamp_outside_the_official_five_minute_window_is_rejected()
    {
        var store = new StubStore(SePayWebhookStoreOutcome.Accepted);
        var handler = CreateHandler(store);
        var staleTimestamp = ServerNow.AddMinutes(-6).ToUnixTimeSeconds();

        var result = await handler.HandleAsync(
            SignedRequest(OfficialPayload(), staleTimestamp),
            ServerNow);

        Assert.Equal(SePayWebhookOutcome.Unauthorized, result.Outcome);
        Assert.Null(store.LastTransaction);
    }

    [Fact]
    public async Task Authenticated_wrong_account_is_durable_reconciliation_evidence()
    {
        var store = new StubStore(SePayWebhookStoreOutcome.ReconciliationRequired);
        var handler = CreateHandler(store);
        var payload = OfficialPayload().Replace(
            "\"accountNumber\":\"0123456789\"",
            "\"accountNumber\":\"9999999999\"",
            StringComparison.Ordinal);

        var result = await handler.HandleAsync(SignedRequest(payload), ServerNow);

        Assert.Equal(SePayWebhookOutcome.Accepted, result.Outcome);
        Assert.Equal(
            SePayReconciliationReasons.UnexpectedAccount,
            store.LastTransaction?.ReconciliationReason);
    }

    [Fact]
    public async Task Authenticated_outgoing_transaction_is_durable_reconciliation_evidence()
    {
        var store = new StubStore(SePayWebhookStoreOutcome.ReconciliationRequired);
        var handler = CreateHandler(store);
        var payload = OfficialPayload().Replace(
            "\"transferType\":\"in\"",
            "\"transferType\":\"out\"",
            StringComparison.Ordinal);

        var result = await handler.HandleAsync(SignedRequest(payload), ServerNow);

        Assert.Equal(SePayWebhookOutcome.Accepted, result.Outcome);
        Assert.Equal(
            SePayReconciliationReasons.DirectionNotIncoming,
            store.LastTransaction?.ReconciliationReason);
    }

    [Fact]
    public async Task Authenticated_malformed_json_is_rejected_before_persistence()
    {
        var store = new StubStore(SePayWebhookStoreOutcome.Accepted);
        var handler = CreateHandler(store);

        var result = await handler.HandleAsync(
            SignedRequest("{not-json}"),
            ServerNow);

        Assert.Equal(SePayWebhookOutcome.InvalidPayload, result.Outcome);
        Assert.Null(store.LastTransaction);
    }

    [Fact]
    public async Task Disabled_adapter_never_authenticates_or_persists()
    {
        var store = new StubStore(SePayWebhookStoreOutcome.Accepted);
        var policy = new SePayWebhookPolicy(
            false,
            string.Empty,
            string.Empty,
            TimeSpan.FromMinutes(5),
            64 * 1024);
        var handler = new SePayWebhookHandler(new SePayWebhookAdapter(policy), store);

        var result = await handler.HandleAsync(
            SignedRequest(OfficialPayload()),
            ServerNow);

        Assert.Equal(SePayWebhookOutcome.Disabled, result.Outcome);
        Assert.Null(store.LastTransaction);
    }

    private static SePayWebhookHandler CreateHandler(StubStore store)
    {
        var policy = new SePayWebhookPolicy(
            true,
            Secret,
            ExpectedAccount,
            TimeSpan.FromMinutes(5),
            64 * 1024);
        return new SePayWebhookHandler(new SePayWebhookAdapter(policy), store);
    }

    private static SePayWebhookRequest SignedRequest(
        string payload,
        long? timestamp = null)
    {
        var timestampValue = (timestamp ?? ServerNow.ToUnixTimeSeconds())
            .ToString(CultureInfo.InvariantCulture);
        var rawBody = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var signature = hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"{timestampValue}.{payload}"));
        return new SePayWebhookRequest(
            rawBody,
            $"sha256={Convert.ToHexString(signature).ToLowerInvariant()}",
            timestampValue);
    }

    private static string OfficialPayload() =>
        """
        {"id":92704,"gateway":"Vietcombank","transactionDate":"2026-10-16 10:30:00","accountNumber":"0123456789","subAccount":"","code":"ZENO-0123456789abcdef","content":"ZENO-0123456789abcdef chuyen tien","transferType":"in","description":"NGUYEN VAN A chuyen tien","transferAmount":5000000,"accumulated":105000000,"referenceCode":"FT24012345678"}
        """;

    private sealed class StubStore(SePayWebhookStoreOutcome outcome) : ISePayWebhookStore
    {
        internal SePayNormalizedTransaction? LastTransaction { get; private set; }

        public Task<SePayWebhookStoreResult> IngestAsync(
            SePayNormalizedTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            LastTransaction = transaction;
            return Task.FromResult(new SePayWebhookStoreResult(outcome, Guid.NewGuid()));
        }
    }
}
