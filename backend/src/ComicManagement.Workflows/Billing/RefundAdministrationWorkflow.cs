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

    public Task<RefundAdministrationResult> RecordTransferAsync(
        Guid refundId,
        Guid actorId,
        long expectedVersion,
        string? bankReference,
        string? evidenceReference,
        string? reportedOutcome,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var reference = bankReference?.Trim().ToUpperInvariant();
        var evidence = evidenceReference?.Trim();
        var key = idempotencyKey?.Trim();
        var validOutcome = reportedOutcome is RefundTransferReportedOutcomes.Succeeded or
            RefundTransferReportedOutcomes.Failed or RefundTransferReportedOutcomes.Unknown;
        if (refundId == Guid.Empty || actorId == Guid.Empty || expectedVersion < 1 ||
            string.IsNullOrWhiteSpace(reference) ||
            reference.Length > RefundTransferRecord.MaximumReferenceLength ||
            string.IsNullOrWhiteSpace(evidence) ||
            evidence.Length > RefundTransferRecord.MaximumEvidenceReferenceLength ||
            !validOutcome || !ValidKey(key, idempotencyKey) ||
            !ValidCorrelationId(correlationId))
        {
            return Task.FromResult(new RefundAdministrationResult(
                RefundAdministrationOutcome.ValidationFailed));
        }

        return store.RecordTransferAsync(new RecordRefundTransferStoreRequest(
            refundId, actorId, expectedVersion, reference, evidence, reportedOutcome!, key!,
            Fingerprint("RefundTransfer", refundId, expectedVersion,
                $"{reference.Length}:{reference}|{evidence.Length}:{evidence}|{reportedOutcome}"),
            correlationId!), cancellationToken);
    }

    public Task<RefundAdministrationResult> ResolveTransferAsync(
        Guid refundId,
        Guid actorId,
        long expectedVersion,
        Guid transferRecordId,
        string? confirmationNote,
        string? idempotencyKey,
        bool succeeded,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var note = confirmationNote?.Trim();
        var key = idempotencyKey?.Trim();
        if (refundId == Guid.Empty || actorId == Guid.Empty || expectedVersion < 1 ||
            transferRecordId == Guid.Empty || string.IsNullOrWhiteSpace(note) ||
            note.Length > RefundTransferRecord.MaximumConfirmationNoteLength ||
            !ValidKey(key, idempotencyKey) || !ValidCorrelationId(correlationId))
        {
            return Task.FromResult(new RefundAdministrationResult(
                RefundAdministrationOutcome.ValidationFailed));
        }

        var operation = succeeded ? "RefundComplete" : "RefundRelease";
        return store.ResolveTransferAsync(new ResolveRefundTransferStoreRequest(
            refundId, actorId, expectedVersion, transferRecordId, note, key!,
            Fingerprint(operation, refundId, expectedVersion,
                $"transfer={transferRecordId:N}|note={note.Length}:{note}"),
            succeeded, correlationId!), cancellationToken);
    }

    private static bool ValidCorrelationId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200;

    private static bool ValidKey(string? normalized, string? original) =>
        !string.IsNullOrWhiteSpace(normalized) && normalized == original &&
        normalized.Length <= RefundTransferRecord.MaximumIdempotencyKeyLength;

    private static string Fingerprint(Guid refundId, long expectedVersion, string note)
        => Fingerprint("RefundApproval", refundId, expectedVersion,
            $"note={note.Length}:{note}");

    private static string Fingerprint(
        string operation,
        Guid refundId,
        long expectedVersion,
        string payload)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{operation}|refund={refundId:N}|version={expectedVersion}|{payload}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
