namespace ComicManagement.Billing.Refunds;

public enum RefundAdministrationOutcome
{
    Succeeded,
    ValidationFailed,
    NotFound,
    StateConflict,
    ConcurrencyConflict,
    RecoveryInsufficient,
    IntegrityConflict,
    IdempotencyConflict,
}

public sealed record RefundAdministrationState(
    Guid RefundId,
    string Status,
    long RequiredRecoveryCoins,
    long Version,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? DecisionReason,
    Guid? ReservationId,
    Guid? WalletOperationId);

public sealed record RefundAdministrationResult(
    RefundAdministrationOutcome Outcome,
    RefundAdministrationState? State = null);

public sealed record ReviewRefundStoreRequest(
    Guid RefundId,
    Guid ActorId,
    string CorrelationId);

public sealed record ApproveRefundStoreRequest(
    Guid RefundId,
    Guid ActorId,
    long ExpectedVersion,
    string DecisionNote,
    string IdempotencyKey,
    string RequestFingerprint,
    string CorrelationId);

public sealed record RejectRefundStoreRequest(
    Guid RefundId,
    Guid ActorId,
    long ExpectedVersion,
    string Reason,
    string CorrelationId);

public interface IRefundAdministrationStore
{
    Task<RefundAdministrationResult> ReviewAsync(
        ReviewRefundStoreRequest request,
        CancellationToken cancellationToken = default);

    Task<RefundAdministrationResult> ApproveAsync(
        ApproveRefundStoreRequest request,
        CancellationToken cancellationToken = default);

    Task<RefundAdministrationResult> RejectAsync(
        RejectRefundStoreRequest request,
        CancellationToken cancellationToken = default);
}
