using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Membership;
using ComicManagement.Billing.Payments;
using ComicManagement.Membership.Memberships;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/membership")]
[Authorize(Policy = AuthorizationPolicies.User)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MembershipController(VipMembershipHandler handler) : ControllerBase
{
    [HttpGet("plan")]
    [ProducesResponseType<VipPlanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VipPlanResponse>> GetPlan(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.GetPlanAsync(cancellationToken);
        return result.Outcome == VipMembershipOutcome.Succeeded && result.Plan is not null
            ? Ok(ToResponse(result.Plan))
            : NotFound(Problem(
                StatusCodes.Status404NotFound,
                "RESOURCE_NOT_FOUND",
                "Resource not found",
                "The active VIP plan is unavailable."));
    }

    [HttpGet("me")]
    [ProducesResponseType<VipMembershipResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<VipMembershipResponse>> GetMe(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.GetMembershipAsync(CurrentUserId(), cancellationToken);
        var state = result.Membership ?? throw new InvalidOperationException(
            "A successful membership query requires state.");
        return Ok(new VipMembershipResponse(state.IsActive, state.ExpiresAt));
    }

    [HttpPost("checkout")]
    [EnableRateLimiting(MembershipRateLimitConstants.VipCheckoutPolicy)]
    [ProducesResponseType<VipCheckoutResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<VipCheckoutResponse>> Checkout(
        VipCheckoutRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(
            PaymentOrder.MaximumIdempotencyKeyLength,
            MinimumLength = 1)]
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.CheckoutAsync(
            CurrentUserId(),
            request.PlanId,
            idempotencyKey,
            cancellationToken);
        if (result.Outcome == VipMembershipOutcome.ValidationFailed)
        {
            return BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "The VIP checkout request or Idempotency-Key is invalid."));
        }

        if (result.Outcome == VipMembershipOutcome.NotFound)
        {
            return NotFound(Problem(
                StatusCodes.Status404NotFound,
                "RESOURCE_NOT_FOUND",
                "Resource not found",
                "The VIP plan is unavailable."));
        }

        if (result.Outcome == VipMembershipOutcome.IdempotencyConflict)
        {
            return Conflict(Problem(
                StatusCodes.Status409Conflict,
                "IDEMPOTENCY_CONFLICT",
                "Idempotency conflict",
                "The Idempotency-Key was already used with a different request."));
        }

        var state = result.Checkout ?? throw new InvalidOperationException(
            "A successful VIP checkout requires state.");
        return StatusCode(
            StatusCodes.Status201Created,
            new VipCheckoutResponse(
                state.OrderId,
                state.AmountVnd,
                state.DurationHours,
                Enum.Parse<VipOrderStatus>(state.Status),
                state.ExpiresAt,
                new PaymentInstructionsResponse(PaymentInstructionType.ProviderSpecific)));
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ApiErrorResponse Problem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>()
        .Create(HttpContext, status, errorCode, title, detail);

    internal static VipPlanResponse ToResponse(VipPlanState plan) => new(
        plan.PlanId,
        plan.PriceVnd,
        plan.DurationHours,
        plan.DiscountPercent,
        plan.Version);
}

public sealed class VipCheckoutRequest
{
    [Required]
    public Guid PlanId { get; init; }
}

public sealed record VipPlanResponse(
    Guid PlanId,
    long PriceVnd,
    int DurationHours,
    int DiscountPercent,
    long Version);

public sealed record VipMembershipResponse(bool IsActive, DateTimeOffset? ExpiresAt);

[JsonConverter(typeof(JsonStringEnumConverter<VipOrderStatus>))]
public enum VipOrderStatus
{
    Pending,
    Expired,
    Succeeded,
}

public sealed record VipCheckoutResponse(
    Guid OrderId,
    long AmountVnd,
    int DurationHours,
    VipOrderStatus Status,
    DateTimeOffset ExpiresAt,
    PaymentInstructionsResponse PaymentInstructions);
