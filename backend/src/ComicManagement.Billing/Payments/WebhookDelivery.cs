namespace ComicManagement.Billing.Payments;

public static class WebhookDeliveryProcessingResults
{
    public const string Accepted = "Accepted";

    public const string Duplicate = "Duplicate";

    public const string ReconciliationRequired = "ReconciliationRequired";
}

public sealed class WebhookDelivery
{
    public const int PayloadReferenceLength = 64;
    public const int MaximumDeliveryReferenceLength = 128;
    public const int MaximumProcessingResultLength = 32;
    public const int MaximumErrorCodeLength = 64;

    private WebhookDelivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid? ProviderTransactionId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public bool AuthVerified { get; private set; }

    public string PayloadReference { get; private set; } = string.Empty;

    public string? DeliveryReference { get; private set; }

    public string ProcessingResult { get; private set; } = string.Empty;

    public string? ErrorCode { get; private set; }

    public static WebhookDelivery CreateAuthenticated(
        Guid id,
        Guid providerTransactionId,
        DateTimeOffset receivedAt,
        string payloadReference,
        string processingResult,
        string? errorCode = null)
    {
        if (id == Guid.Empty || providerTransactionId == Guid.Empty)
        {
            throw new ArgumentException("Identifiers are required.");
        }

        if (receivedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(receivedAt));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(payloadReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingResult);

        return new WebhookDelivery
        {
            Id = id,
            ProviderTransactionId = providerTransactionId,
            ReceivedAt = receivedAt,
            AuthVerified = true,
            PayloadReference = payloadReference,
            ProcessingResult = processingResult,
            ErrorCode = errorCode,
        };
    }
}
