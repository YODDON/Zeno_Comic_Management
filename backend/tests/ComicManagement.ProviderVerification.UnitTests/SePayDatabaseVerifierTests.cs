using ComicManagement.ProviderVerification;
using Xunit;

namespace ComicManagement.ProviderVerification.UnitTests;

public sealed class SePayDatabaseVerifierTests
{
    [Fact]
    public void Success_replay_requires_exactly_once_financial_state_and_duplicate_delivery()
    {
        var result = SePayDatabaseVerifier.Verify(
            SePayVerificationScenario.SuccessReplay,
            Observation("Fulfilled", null, true, 2, 1, "Succeeded", "Verified", "Fulfilled",
                1, 1, 10_000, 1),
            200);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Retry_requires_final_success_without_double_credit()
    {
        var result = SePayDatabaseVerifier.Verify(
            SePayVerificationScenario.Retry,
            Observation("Fulfilled", null, true, 3, 1, "Succeeded", "Verified", "Fulfilled",
                1, 1, 10_000, 1),
            200,
            503);

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData(SePayVerificationScenario.UnknownOrder, "ORDER_NOT_FOUND", false, null)]
    [InlineData(SePayVerificationScenario.AmountMismatch, "AMOUNT_MISMATCH", true, "Pending")]
    [InlineData(SePayVerificationScenario.LatePayment, "LatePayment", true, "Expired")]
    public void Reconciliation_scenarios_require_durable_evidence_and_zero_financial_mutation(
        SePayVerificationScenario scenario,
        string reason,
        bool matched,
        string? orderStatus)
    {
        var result = SePayDatabaseVerifier.Verify(
            scenario,
            Observation("ReconciliationRequired", reason, matched, 1, 0, orderStatus),
            200);

        Assert.True(result.Passed);
        Assert.True(result.Checks["financialSideEffectsMatchScenario"]);
    }

    [Fact]
    public void Invalid_signature_requires_no_persistence_and_zero_financial_mutation()
    {
        var result = SePayDatabaseVerifier.Verify(
            SePayVerificationScenario.InvalidSignature,
            new(false, null, null, false, 0, 0, true, "Pending", "Pending", "NotStarted",
                0, 0, 0, 0, 10_000),
            401);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Any_negative_scenario_with_financial_mutation_fails_closed()
    {
        var result = SePayDatabaseVerifier.Verify(
            SePayVerificationScenario.AmountMismatch,
            Observation("ReconciliationRequired", "AMOUNT_MISMATCH", true, 1, 0, "Pending",
                lots: 1),
            200);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Retry_without_observed_non_success_attempt_is_inconclusive()
    {
        var result = SePayDatabaseVerifier.Verify(
            SePayVerificationScenario.Retry,
            Observation("Fulfilled", null, true, 2, 1, "Succeeded", "Verified", "Fulfilled",
                1, 1, 10_000, 1),
            200,
            200);

        Assert.False(result.Passed);
    }

    private static SePayDatabaseObservation Observation(
        string processingStatus,
        string? reason,
        bool matched,
        long deliveries,
        long duplicateDeliveries,
        string? orderStatus,
        string providerPaymentStatus = "Pending",
        string fulfillmentStatus = "NotStarted",
        long lots = 0,
        long operations = 0,
        long ledger = 0,
        long outbox = 0) => new(
            true,
            processingStatus,
            reason,
            matched,
            deliveries,
            duplicateDeliveries,
            orderStatus is not null,
            orderStatus,
            providerPaymentStatus,
            fulfillmentStatus,
            lots,
            operations,
            ledger,
            outbox,
            10_000);
}
