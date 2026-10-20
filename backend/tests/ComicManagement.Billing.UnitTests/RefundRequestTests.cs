using ComicManagement.Billing.Refunds;
using System.Security.Cryptography;
using System.Text;
using ComicManagement.Billing.Payments;
using Xunit;

namespace ComicManagement.Billing.UnitTests;

public sealed class RefundRequestTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Factory_snapshots_five_percent_fee_and_full_recovery()
    {
        var request = RefundRequest.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Duplicate transfer",
            100_000,
            10_000,
            "refund-key",
            new string('a', 64),
            "v1",
            Now);

        Assert.Equal(5_000, request.FeeAmountVnd);
        Assert.Equal(95_000, request.NetRefundVnd);
        Assert.Equal(10_000, request.RequiredRecoveryCoins);
        Assert.Equal(RefundRequestStatuses.Requested, request.Status);
        Assert.Equal(1, request.Version);
    }

    [Fact]
    public void Eligibility_includes_exact_seven_day_boundary_and_expires_after_it()
    {
        var paidAt = Now.AddDays(-RefundRequest.RequestWindowDays);
        var snapshot = new RefundEligibilitySnapshot(
            Guid.NewGuid(),
            PaymentOrderTypes.TopUp,
            TopUpOrderStatuses.Succeeded,
            ProviderPaymentStatuses.Verified,
            PaymentFulfillmentStatuses.Fulfilled,
            100_000,
            10_000,
            paidAt,
            10_000,
            0,
            0,
            0,
            false);

        var atBoundary = RefundEligibilityPolicy.Evaluate(snapshot, Now);
        var afterBoundary = RefundEligibilityPolicy.Evaluate(snapshot, Now.AddTicks(10));

        Assert.Equal(RefundRequestOutcome.Succeeded, atBoundary.Outcome);
        Assert.True(atBoundary.State!.Eligible);
        Assert.Equal(RefundRequestOutcome.WindowExpired, afterBoundary.Outcome);
        Assert.False(afterBoundary.State!.Eligible);
    }

    [Fact]
    public async Task Handler_normalizes_recipient_protects_plaintext_and_uses_server_store()
    {
        var store = new CapturingStore();
        var protector = new CapturingProtector();
        var handler = new RefundRequestHandler(store, protector, new TestFingerprintService());
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var result = await handler.CreateAsync(
            userId,
            orderId,
            "  duplicate transfer  ",
            new RefundRecipientInput(" vcb ", " 0123456789 ", " nguyen   van a "),
            "refund-key");

        Assert.Equal(RefundRequestOutcome.Succeeded, result.Outcome);
        var request = Assert.IsType<CreateRefundStoreRequest>(store.Request);
        Assert.Equal("duplicate transfer", request.Reason);
        Assert.Equal("VCB", request.BankCode);
        Assert.Equal("6789", request.AccountNumberLast4);
        Assert.Equal("******6789", request.AccountNumberMasked);
        Assert.Equal("N*** V*** A***", request.AccountHolderMasked);
        Assert.Equal("0123456789", protector.AccountNumber);
        Assert.Equal("NGUYEN VAN A", protector.AccountHolder);
        Assert.Matches("^[0-9a-f]{64}$", request.Fingerprints.CurrentFingerprint);
        Assert.Equal("v1", request.Fingerprints.CurrentVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  key")]
    public async Task Handler_rejects_invalid_idempotency_key(string key)
    {
        var store = new CapturingStore();
        var handler = new RefundRequestHandler(
            store,
            new CapturingProtector(),
            new TestFingerprintService());

        var result = await handler.CreateAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "reason",
            new RefundRecipientInput("VCB", "0123456789", "NGUYEN VAN A"),
            key);

        Assert.Equal(RefundRequestOutcome.ValidationFailed, result.Outcome);
        Assert.Null(store.Request);
    }

    private sealed class CapturingProtector : IRefundRecipientProtector
    {
        public string? AccountNumber { get; private set; }

        public string? AccountHolder { get; private set; }

        public ProtectedRefundRecipient Protect(string accountNumber, string accountHolder)
        {
            AccountNumber = accountNumber;
            AccountHolder = accountHolder;
            return new ProtectedRefundRecipient([1], [2], "test-v1");
        }

        public UnprotectedRefundRecipient Unprotect(
            byte[] accountNumberCiphertext,
            byte[] accountHolderCiphertext,
            string protectionVersion) => throw new NotSupportedException();
    }

    private sealed class TestFingerprintService : IRefundRequestFingerprintService
    {
        public RefundRequestFingerprints CreateFingerprints(string canonicalRequest) => new(
            "v1",
            Fingerprint(canonicalRequest),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["v1"] = Fingerprint(canonicalRequest),
            });

        private static string Fingerprint(string canonicalRequest) => Convert.ToHexString(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes("rf01-unit-test-fingerprint-key"),
                    Encoding.UTF8.GetBytes(canonicalRequest)))
            .ToLowerInvariant();
    }

    private sealed class CapturingStore : IRefundRequestStore
    {
        public CreateRefundStoreRequest? Request { get; private set; }

        public Task<RefundRequestResult> CreateOrReplayAsync(
            CreateRefundStoreRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new RefundRequestResult(
                RefundRequestOutcome.Succeeded,
                new RefundRequestState(
                    request.RefundId,
                    request.PaymentOrderId,
                    RefundRequestStatuses.Requested,
                    request.Reason,
                    100_000,
                    5_000,
                    95_000,
                    10_000,
                    Now,
                    request.BankCode,
                    request.AccountNumberMasked,
                    request.AccountHolderMasked)));
        }

        public Task<RefundEligibilityResult> GetEligibilityAsync(
            Guid userId,
            Guid paymentOrderId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RefundRequestState?> GetAsync(
            Guid userId,
            Guid refundId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RefundRequestState>> ListAsync(
            Guid userId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
