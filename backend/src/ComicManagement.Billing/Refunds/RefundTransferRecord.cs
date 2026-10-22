namespace ComicManagement.Billing.Refunds;

public static class RefundTransferReportedOutcomes
{
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";
}

public static class RefundTransferStatuses
{
    public const string ReportedSucceeded = "ReportedSucceeded";
    public const string ReportedFailed = "ReportedFailed";
    public const string NeedsReconciliation = "NeedsReconciliation";
    public const string VerifiedSucceeded = "VerifiedSucceeded";
    public const string VerifiedFailed = "VerifiedFailed";
}

public sealed class RefundTransferRecord
{
    public const int MaximumReferenceLength = 128;
    public const int MaximumEvidenceReferenceLength = 500;
    public const int MaximumStatusLength = 32;
    public const int MaximumIdempotencyKeyLength = 200;
    public const int FingerprintLength = 64;
    public const int MaximumConfirmationNoteLength = 1000;

    private RefundTransferRecord() { }

    public Guid Id { get; private set; }
    public Guid RefundRequestId { get; private set; }
    public Guid UserId { get; private set; }
    public string BankReference { get; private set; } = string.Empty;
    public long AmountVnd { get; private set; }
    public string EvidenceReference { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public Guid RecordedBy { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestFingerprint { get; private set; } = string.Empty;
    public Guid? VerifiedBy { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public string? ConfirmationNote { get; private set; }
    public string? VerificationIdempotencyKey { get; private set; }
    public string? VerificationFingerprint { get; private set; }
    public Guid? ResolutionOperationId { get; private set; }

    public static RefundTransferRecord Create(
        Guid id,
        Guid refundRequestId,
        Guid userId,
        string bankReference,
        long amountVnd,
        string evidenceReference,
        string reportedOutcome,
        Guid recordedBy,
        DateTimeOffset recordedAt,
        string idempotencyKey,
        string requestFingerprint)
    {
        EnsureId(id, nameof(id));
        EnsureId(refundRequestId, nameof(refundRequestId));
        EnsureId(userId, nameof(userId));
        EnsureText(bankReference, MaximumReferenceLength, nameof(bankReference));
        EnsureText(evidenceReference, MaximumEvidenceReferenceLength, nameof(evidenceReference));
        EnsureId(recordedBy, nameof(recordedBy));
        EnsureUtc(recordedAt, nameof(recordedAt));
        EnsureText(idempotencyKey, MaximumIdempotencyKeyLength, nameof(idempotencyKey));
        EnsureFingerprint(requestFingerprint, nameof(requestFingerprint));
        if (amountVnd <= 0) throw new ArgumentOutOfRangeException(nameof(amountVnd));

        var status = reportedOutcome switch
        {
            RefundTransferReportedOutcomes.Succeeded => RefundTransferStatuses.ReportedSucceeded,
            RefundTransferReportedOutcomes.Failed => RefundTransferStatuses.ReportedFailed,
            RefundTransferReportedOutcomes.Unknown => RefundTransferStatuses.NeedsReconciliation,
            _ => throw new ArgumentException("Reported outcome is invalid.", nameof(reportedOutcome)),
        };

        return new RefundTransferRecord
        {
            Id = id,
            RefundRequestId = refundRequestId,
            UserId = userId,
            BankReference = bankReference,
            AmountVnd = amountVnd,
            EvidenceReference = evidenceReference,
            Status = status,
            RecordedBy = recordedBy,
            RecordedAt = recordedAt,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
        };
    }

    public void Verify(
        bool succeeded,
        Guid actorId,
        string confirmationNote,
        string idempotencyKey,
        string fingerprint,
        Guid resolutionOperationId,
        DateTimeOffset verifiedAt)
    {
        EnsureId(actorId, nameof(actorId));
        EnsureText(confirmationNote, MaximumConfirmationNoteLength, nameof(confirmationNote));
        EnsureText(idempotencyKey, MaximumIdempotencyKeyLength, nameof(idempotencyKey));
        EnsureFingerprint(fingerprint, nameof(fingerprint));
        EnsureId(resolutionOperationId, nameof(resolutionOperationId));
        EnsureUtc(verifiedAt, nameof(verifiedAt));
        var expected = succeeded
            ? RefundTransferStatuses.ReportedSucceeded
            : RefundTransferStatuses.ReportedFailed;
        if (Status != expected && Status != RefundTransferStatuses.NeedsReconciliation)
        {
            throw new InvalidOperationException(
                "The reported transfer outcome does not support this verification.");
        }

        Status = succeeded
            ? RefundTransferStatuses.VerifiedSucceeded
            : RefundTransferStatuses.VerifiedFailed;
        VerifiedBy = actorId;
        VerifiedAt = verifiedAt;
        ConfirmationNote = confirmationNote;
        VerificationIdempotencyKey = idempotencyKey;
        VerificationFingerprint = fingerprint;
        ResolutionOperationId = resolutionOperationId;
    }

    private static void EnsureId(Guid value, string name)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier is required.", name);
    }

    private static void EnsureText(string value, int max, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value != value.Trim() || value.Length > max) throw new ArgumentOutOfRangeException(name);
    }

    private static void EnsureFingerprint(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != FingerprintLength || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Fingerprint must be a SHA-256 hex digest.", name);
    }

    private static void EnsureUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.", name);
    }
}
