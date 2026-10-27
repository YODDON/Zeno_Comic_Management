using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Billing;
using ComicManagement.Api.Serialization;
using ComicManagement.Administration.Finance;
using ComicManagement.Billing.Refunds;
using ComicManagement.Workflows.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin/refunds")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminRefundsController(
    RefundAdministrationWorkflow workflow,
    FinanceAdministrationQueryHandler queries) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AdminFinancePage<AdminRefundListItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminFinancePage<AdminRefundListItem>>> List(
        [FromQuery, StringLength(FinanceAdministrationQueryHandler.MaximumSearchLength)] string? search,
        [FromQuery] string? status,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, FinanceAdministrationQueryHandler.MaximumPageSize)] int pageSize =
            FinanceAdministrationQueryHandler.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await queries.ListRefundsAsync(search, status, page, pageSize, cancellationToken);
        return result.Outcome == FinanceAdministrationQueryOutcome.Succeeded && result.Value is not null
            ? Ok(result.Value)
            : BadRequest(Problem(400, "VALIDATION_FAILED", "Validation failed",
                "The refund list criteria are invalid."));
    }

    [HttpGet("{refundId:guid}")]
    [ProducesResponseType<AdminRefundDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminRefundDetail>> Get(
        Guid refundId,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await queries.GetRefundAsync(refundId, cancellationToken);
        return result.Outcome switch
        {
            FinanceAdministrationQueryOutcome.Succeeded when result.Value is not null => Ok(result.Value),
            FinanceAdministrationQueryOutcome.NotFound => NotFound(Problem(
                404, "RESOURCE_NOT_FOUND", "Resource not found", "The refund request was not found.")),
            _ => BadRequest(Problem(
                400, "VALIDATION_FAILED", "Validation failed", "The refund identifier is invalid.")),
        };
    }

    [HttpPost("{refundId:guid}/review")]
    [ProducesResponseType<AdminRefundResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(BillingRateLimitConstants.RefundAdminMutationPolicy)]
    public async Task<ActionResult<AdminRefundResponse>> Review(
        Guid refundId,
        CancellationToken cancellationToken) => ToResponse(await workflow.ReviewAsync(
            refundId,
            ActorId(),
            HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPost("{refundId:guid}/approve")]
    [ProducesResponseType<AdminRefundResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(BillingRateLimitConstants.RefundAdminMutationPolicy)]
    public async Task<ActionResult<AdminRefundResponse>> Approve(
        Guid refundId,
        ApproveRefundRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(
            200, MinimumLength = 1)] string idempotencyKey,
        CancellationToken cancellationToken) => ToResponse(await workflow.ApproveAsync(
            refundId,
            ActorId(),
            request.ExpectedVersion,
            request.DecisionNote,
            idempotencyKey,
            HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPost("{refundId:guid}/reject")]
    [ProducesResponseType<AdminRefundResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(BillingRateLimitConstants.RefundAdminMutationPolicy)]
    public async Task<ActionResult<AdminRefundResponse>> Reject(
        Guid refundId,
        RejectRefundRequest request,
        CancellationToken cancellationToken) => ToResponse(await workflow.RejectAsync(
            refundId,
            ActorId(),
            request.ExpectedVersion,
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPost("{refundId:guid}/transfer-attempts")]
    [ProducesResponseType<AdminRefundResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(BillingRateLimitConstants.RefundAdminMutationPolicy)]
    public async Task<ActionResult<AdminRefundResponse>> RecordTransfer(
        Guid refundId,
        RecordRefundTransferRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(200, MinimumLength = 1)]
        string idempotencyKey,
        CancellationToken cancellationToken) => ToResponse(await workflow.RecordTransferAsync(
            refundId, ActorId(), request.ExpectedVersion, request.BankReference,
            request.EvidenceReference, request.ReportedOutcome?.ToString(), idempotencyKey,
            HttpContext.TraceIdentifier, cancellationToken));

    [HttpPost("{refundId:guid}/complete")]
    [ProducesResponseType<AdminRefundResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(BillingRateLimitConstants.RefundAdminMutationPolicy)]
    public async Task<ActionResult<AdminRefundResponse>> Complete(
        Guid refundId,
        ResolveRefundTransferRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(200, MinimumLength = 1)]
        string idempotencyKey,
        CancellationToken cancellationToken) => ToResponse(await workflow.ResolveTransferAsync(
            refundId, ActorId(), request.ExpectedVersion, request.TransferRecordId,
            request.ConfirmationNote, idempotencyKey, true, HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPost("{refundId:guid}/release")]
    [ProducesResponseType<AdminRefundResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(BillingRateLimitConstants.RefundAdminMutationPolicy)]
    public async Task<ActionResult<AdminRefundResponse>> Release(
        Guid refundId,
        ResolveRefundTransferRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(200, MinimumLength = 1)]
        string idempotencyKey,
        CancellationToken cancellationToken) => ToResponse(await workflow.ResolveTransferAsync(
            refundId, ActorId(), request.ExpectedVersion, request.TransferRecordId,
            request.ConfirmationNote, idempotencyKey, false, HttpContext.TraceIdentifier,
            cancellationToken));

    private ActionResult<AdminRefundResponse> ToResponse(RefundAdministrationResult result) =>
        result.Outcome switch
        {
            RefundAdministrationOutcome.Succeeded when result.State is not null =>
                Ok(Map(result.State)),
            RefundAdministrationOutcome.ValidationFailed => BadRequest(Problem(
                400, "VALIDATION_FAILED", "Validation failed",
                "The refund transition request is invalid.")),
            RefundAdministrationOutcome.NotFound => NotFound(Problem(
                404, "RESOURCE_NOT_FOUND", "Resource not found",
                "The refund request was not found.")),
            RefundAdministrationOutcome.ConcurrencyConflict => Conflict(Problem(
                409, "CONCURRENCY_CONFLICT", "Concurrency conflict",
                "The refund request changed. Reload it before retrying.")),
            RefundAdministrationOutcome.StateConflict => Conflict(Problem(
                409, "REFUND_STATE_CONFLICT", "Refund state conflict",
                "The refund request is not in a valid state for this transition.")),
            RefundAdministrationOutcome.IntegrityConflict => Conflict(Problem(
                409, "FINANCIAL_INTEGRITY_CONFLICT", "Financial integrity conflict",
                "The financial state requires reconciliation before approval.")),
            RefundAdministrationOutcome.IdempotencyConflict => Conflict(Problem(
                409, "IDEMPOTENCY_CONFLICT", "Idempotency conflict",
                "The Idempotency-Key was already used with a different approval request.")),
            RefundAdministrationOutcome.TransferEvidenceConflict => Conflict(Problem(
                409, "TRANSFER_REFERENCE_CONFLICT", "Transfer evidence conflict",
                "The bank transfer reference is already associated with another refund.")),
            RefundAdministrationOutcome.RecoveryInsufficient => UnprocessableEntity(Problem(
                422, "REFUND_RECOVERY_INSUFFICIENT", "Recovery is insufficient",
                "The exact original Paid Coin lot is no longer fully recoverable.")),
            RefundAdministrationOutcome.TransferNotVerified => UnprocessableEntity(Problem(
                422, "REFUND_TRANSFER_NOT_VERIFIED", "Transfer evidence is not verified",
                "The recorded transfer outcome does not support this transition.")),
            _ => Conflict(Problem(
                409, "REFUND_STATE_CONFLICT", "Refund state conflict",
                "The refund transition could not be completed.")),
        };

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static AdminRefundResponse Map(RefundAdministrationState state) => new(
        state.RefundId,
        Enum.Parse<RefundStatus>(state.Status),
        state.RequiredRecoveryCoins,
        state.Version,
        state.ReviewedBy,
        state.ReviewedAt,
        state.ApprovedBy,
        state.ApprovedAt,
        state.DecisionReason,
        state.ReservationId,
        state.WalletOperationId,
        state.TransferRecordId,
        state.TransferStatus is null ? null : Enum.Parse<RefundTransferStatus>(state.TransferStatus),
        state.ResolutionOperationId);

    private ApiErrorResponse Problem(int status, string code, string title, string detail) =>
        HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>()
            .Create(HttpContext, status, code, title, detail);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ApproveRefundRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required, StringLength(RefundRequest.MaximumReasonLength, MinimumLength = 1)]
    public string DecisionNote { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RejectRefundRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required, StringLength(RefundRequest.MaximumReasonLength, MinimumLength = 1)]
    public string Reason { get; init; } = string.Empty;
}

[JsonConverter(typeof(StringOnlyEnumConverter<RefundTransferReportedOutcome>))]
public enum RefundTransferReportedOutcome
{
    Succeeded,
    Failed,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<RefundTransferStatus>))]
public enum RefundTransferStatus
{
    ReportedSucceeded,
    ReportedFailed,
    NeedsReconciliation,
    VerifiedSucceeded,
    VerifiedFailed,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RecordRefundTransferRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    [Required, StringLength(RefundTransferRecord.MaximumReferenceLength, MinimumLength = 1)]
    public string BankReference { get; init; } = string.Empty;

    [Required, StringLength(RefundTransferRecord.MaximumEvidenceReferenceLength, MinimumLength = 1)]
    public string EvidenceReference { get; init; } = string.Empty;

    [Required]
    public RefundTransferReportedOutcome? ReportedOutcome { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ResolveRefundTransferRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    public Guid TransferRecordId { get; init; }

    [Required, StringLength(RefundTransferRecord.MaximumConfirmationNoteLength, MinimumLength = 1)]
    public string ConfirmationNote { get; init; } = string.Empty;
}

public sealed record AdminRefundResponse(
    Guid RefundId,
    RefundStatus Status,
    long RequiredRecoveryCoins,
    long Version,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? DecisionReason,
    Guid? ReservationId,
    Guid? WalletOperationId,
    Guid? TransferRecordId,
    RefundTransferStatus? TransferStatus,
    Guid? ResolutionOperationId);
