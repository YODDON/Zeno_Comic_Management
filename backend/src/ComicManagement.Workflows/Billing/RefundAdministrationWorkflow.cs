using ComicManagement.Billing.Refunds;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ComicManagement.Workflows.Billing;

public sealed class RefundAdministrationWorkflow(IRefundAdministrationStore store)
{
    public Task<RefundAdministrationResult> ReviewAsync(
        Guid refundId,
        Guid actorId,
        string? correlationId,
        CancellationToken cancellationToken = default) =>
        refundId == Guid.Empty || actorId == Guid.Empty || !ValidCorrelationId(correlationId)
            ? Task.FromResult(new RefundAdministrationResult(
                RefundAdministrationOutcome.ValidationFailed))
            : store.ReviewAsync(
                new ReviewRefundStoreRequest(refundId, actorId, correlationId!),
                cancellationToken);

    public Task<RefundAdministrationResult> ApproveAsync(
        Guid refundId,
        Guid actorId,
        long expectedVersion,
        string? decisionNote,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var normalizedNote = decisionNote?.Trim();
        var normalizedKey = idempotencyKey?.Trim();
        return refundId == Guid.Empty || actorId == Guid.Empty || expectedVersion < 1 ||
               string.IsNullOrWhiteSpace(normalizedNote) ||
               normalizedNote.Length > RefundRequest.MaximumReasonLength ||
               string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey != idempotencyKey ||
               normalizedKey.Length > 200 ||
               !ValidCorrelationId(correlationId)
            ? Task.FromResult(new RefundAdministrationResult(
                RefundAdministrationOutcome.ValidationFailed))
            : store.ApproveAsync(
                new ApproveRefundStoreRequest(
                    refundId,
                    actorId,
                    expectedVersion,
                    normalizedNote,
                    normalizedKey,
                    Fingerprint(refundId, expectedVersion, normalizedNote),
                    correlationId!),
                cancellationToken);
    }

    public Task<RefundAdministrationResult> RejectAsync(
        Guid refundId,
        Guid actorId,
        long expectedVersion,
        string? reason,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = reason?.Trim();
        return refundId == Guid.Empty || actorId == Guid.Empty || expectedVersion < 1 ||
               string.IsNullOrWhiteSpace(normalizedReason) ||
               normalizedReason.Length > RefundRequest.MaximumReasonLength ||
               !ValidCorrelationId(correlationId)
            ? Task.FromResult(new RefundAdministrationResult(
                RefundAdministrationOutcome.ValidationFailed))
            : store.RejectAsync(
                new RejectRefundStoreRequest(
                    refundId,
                    actorId,
                    expectedVersion,
                    normalizedReason,
                    correlationId!),
                cancellationToken);
    }

    private static bool ValidCorrelationId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200;

    private static string Fingerprint(Guid refundId, long expectedVersion, string note)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"RefundApproval|refund={refundId:N}|version={expectedVersion}|note={note.Length}:{note}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
