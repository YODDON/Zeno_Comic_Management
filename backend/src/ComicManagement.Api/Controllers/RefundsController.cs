using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Billing;
using ComicManagement.Billing.Refunds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/refunds")]
[Authorize(Policy = AuthorizationPolicies.User)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RefundsController(RefundRequestHandler handler) : ControllerBase
{
    [HttpGet("eligibility")]
    [ProducesResponseType<RefundEligibilityResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RefundEligibilityResponse>> GetEligibility(
        [FromQuery] Guid paymentOrderId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.GetEligibilityAsync(
            CurrentUserId(), paymentOrderId, cancellationToken);
        if (result.State is { } state)
        {
            return Ok(new RefundEligibilityResponse(
                state.OrderId,
                state.OriginalAmountVnd,
                state.FeeVnd,
                state.NetRefundVnd,
                state.LotGrantedCoins,
                state.LotAvailableCoins,
                state.Eligible,
                state.ReasonCode,
                state.PaidAt));
        }

        return result.Outcome switch
        {
            RefundRequestOutcome.ValidationFailed => BadRequest(Problem(
                StatusCodes.Status400BadRequest, "VALIDATION_FAILED",
                "Validation failed", "A non-empty paymentOrderId is required.")),
            RefundRequestOutcome.NotFound => NotFound(Problem(
                StatusCodes.Status404NotFound, "RESOURCE_NOT_FOUND",
                "Resource not found", "The TopUp order was not found.")),
            _ => UnprocessableEntity(Problem(
                StatusCodes.Status422UnprocessableEntity, "REFUND_ORDER_NOT_ELIGIBLE",
                "Refund order is not eligible",
                "The order is not a verified fulfilled TopUp with its original Paid Coin lot.")),
        };
    }

    [HttpPost]
    [EnableRateLimiting(BillingRateLimitConstants.RefundCreatePolicy)]
    [ProducesResponseType<CreateRefundResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CreateRefundResponse>> Create(
        CreateRefundRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(
            RefundRequest.MaximumIdempotencyKeyLength,
            MinimumLength = 1)] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.CreateAsync(
            CurrentUserId(),
            request.PaymentOrderId,
            request.Reason,
            request.Recipient is null
                ? null
                : new RefundRecipientInput(
                    request.Recipient.BankCode,
                    request.Recipient.AccountNumber,
                    request.Recipient.AccountHolder),
            idempotencyKey,
            cancellationToken);
        if (result.State is { } state)
        {
            return StatusCode(StatusCodes.Status201Created, new CreateRefundResponse(
                state.RefundId,
                CreatedRefundStatus.Requested,
                state.OriginalAmountVnd,
                state.FeeVnd,
                state.NetRefundVnd,
                state.RequestedAt));
        }

        return MapFailure(result.Outcome);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RefundDetailResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<RefundDetailResponse>>> List(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var states = await handler.ListAsync(CurrentUserId(), cancellationToken);
        return Ok(states.Select(MapDetail).ToArray());
    }

    [HttpGet("{refundId:guid}")]
    [ProducesResponseType<RefundDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RefundDetailResponse>> Get(
        Guid refundId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var state = await handler.GetAsync(CurrentUserId(), refundId, cancellationToken);
        return state is null
            ? NotFound(Problem(
                StatusCodes.Status404NotFound,
                "RESOURCE_NOT_FOUND",
                "Resource not found",
                "The refund request was not found."))
            : Ok(MapDetail(state));
    }

    private ActionResult<CreateRefundResponse> MapFailure(RefundRequestOutcome outcome) =>
        outcome switch
        {
            RefundRequestOutcome.ValidationFailed => BadRequest(Problem(
                400, "VALIDATION_FAILED", "Validation failed",
                "The refund request or Idempotency-Key is invalid.")),
            RefundRequestOutcome.NotFound => NotFound(Problem(
                404, "RESOURCE_NOT_FOUND", "Resource not found",
                "The TopUp order was not found.")),
            RefundRequestOutcome.IdempotencyConflict => Conflict(Problem(
                409, "IDEMPOTENCY_CONFLICT", "Idempotency conflict",
                "The Idempotency-Key was already used with a different request.")),
            RefundRequestOutcome.RefundAlreadyExists => Conflict(Problem(
                409, "REFUND_ALREADY_EXISTS", "Refund already exists",
                "This TopUp already has its one permitted refund request.")),
            RefundRequestOutcome.WindowExpired => UnprocessableEntity(Problem(
                422, "REFUND_WINDOW_EXPIRED", "Refund window expired",
                "The seven-day refund request window has expired.")),
            RefundRequestOutcome.RecoveryInsufficient => UnprocessableEntity(Problem(
                422, "REFUND_RECOVERY_INSUFFICIENT", "Recovery is insufficient",
                "The original Paid Coin lot is no longer fully recoverable.")),
            _ => UnprocessableEntity(Problem(
                422, "REFUND_ORDER_NOT_ELIGIBLE", "Refund order is not eligible",
                "Only a verified fulfilled TopUp can be refunded.")),
        };

    private static RefundDetailResponse MapDetail(RefundRequestState state) => new(
        state.RefundId,
        state.PaymentOrderId,
        Enum.Parse<RefundStatus>(state.Status),
        state.Reason,
        state.OriginalAmountVnd,
        state.FeeVnd,
        state.NetRefundVnd,
        state.RequiredRecoveryCoins,
        state.RequestedAt,
        new MaskedRefundRecipientResponse(
            state.BankCode,
            state.AccountNumberMasked,
            state.AccountHolderMasked));

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ApiErrorResponse Problem(int status, string code, string title, string detail) =>
        HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>()
            .Create(HttpContext, status, code, title, detail);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateRefundRequest
{
    [Required, JsonRequired]
    public Guid PaymentOrderId { get; init; }

    [Required, JsonRequired, StringLength(RefundRequest.MaximumReasonLength, MinimumLength = 1)]
    public string Reason { get; init; } = string.Empty;

    [Required, JsonRequired]
    public RefundRecipientRequest Recipient { get; init; } = null!;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RefundRecipientRequest
{
    [Required, JsonRequired, StringLength(RefundRecipient.MaximumBankCodeLength, MinimumLength = 2)]
    public string BankCode { get; init; } = string.Empty;

    [Required, JsonRequired, StringLength(RefundRecipient.MaximumAccountNumberLength, MinimumLength = 4)]
    public string AccountNumber { get; init; } = string.Empty;

    [Required, JsonRequired, StringLength(RefundRecipient.MaximumAccountHolderLength, MinimumLength = 2)]
    public string AccountHolder { get; init; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter<RefundStatus>))]
public enum RefundStatus
{
    Requested,
    UnderReview,
    RecoveryReserved,
    TransferPending,
    Completed,
    Rejected,
    NeedsReconciliation,
    TransferConfirmedFailed,
    ReservationReleased,
}

[JsonConverter(typeof(JsonStringEnumConverter<CreatedRefundStatus>))]
public enum CreatedRefundStatus
{
    Requested,
}

public sealed record RefundEligibilityResponse(
    Guid OrderId,
    long OriginalAmountVnd,
    long FeeVnd,
    long NetRefundVnd,
    long LotGrantedCoins,
    long LotAvailableCoins,
    bool Eligible,
    string? ReasonCode,
    DateTimeOffset PaidAt);

public sealed record CreateRefundResponse(
    Guid RefundId,
    CreatedRefundStatus Status,
    long OriginalAmountVnd,
    long FeeVnd,
    long NetRefundVnd,
    DateTimeOffset RequestedAt);

public sealed record RefundDetailResponse(
    Guid RefundId,
    Guid PaymentOrderId,
    RefundStatus Status,
    string Reason,
    long OriginalAmountVnd,
    long FeeVnd,
    long NetRefundVnd,
    long RequiredRecoveryCoins,
    DateTimeOffset RequestedAt,
    MaskedRefundRecipientResponse Recipient);

public sealed record MaskedRefundRecipientResponse(
    string BankCode,
    string AccountNumberMasked,
    string AccountHolderMasked);
