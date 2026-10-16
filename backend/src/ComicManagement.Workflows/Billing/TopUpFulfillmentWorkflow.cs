using ComicManagement.Billing.Payments;

namespace ComicManagement.Workflows.Billing;

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

public sealed class TopUpFulfillmentWorkflow(
    SePayWebhookAdapter adapter,
    ISePayWebhookStore store)
{
    public async Task<SePayWebhookResult> HandleAsync(
        SePayWebhookRequest request,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        var verification = adapter.VerifyAndNormalize(request, verifiedAt);
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

        var stored = await store.IngestAsync(
            verification.Transaction,
            verifiedAt,
            cancellationToken);
        return new SePayWebhookResult(SePayWebhookOutcome.Accepted, stored.Outcome);
    }
}
