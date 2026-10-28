using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Membership;
using ComicManagement.Membership.Memberships;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin/vip-plan")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminVipPlanController(VipMembershipHandler handler) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<VipPlanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VipPlanResponse>> Get(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.GetPlanAsync(cancellationToken);
        return result.Outcome == VipMembershipOutcome.Succeeded && result.Plan is not null
            ? Ok(MembershipController.ToResponse(result.Plan))
            : NotFound(Problem(
                StatusCodes.Status404NotFound,
                "RESOURCE_NOT_FOUND",
                "Resource not found",
                "The active VIP plan is unavailable."));
    }

    [HttpPatch]
    [EnableRateLimiting(MembershipRateLimitConstants.AdminPlanMutationPolicy)]
    [ProducesResponseType<VipPlanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<VipPlanResponse>> Patch(
        UpdateVipPlanPriceRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.UpdateFutureOrderPriceAsync(
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            request.PriceVnd,
            request.ExpectedVersion,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return result.Outcome switch
        {
            VipMembershipOutcome.Succeeded when result.Plan is not null =>
                Ok(MembershipController.ToResponse(result.Plan)),
            VipMembershipOutcome.ValidationFailed => BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "The VIP price or expected version is invalid.")),
            VipMembershipOutcome.ConcurrencyConflict => Conflict(Problem(
                StatusCodes.Status409Conflict,
                "CONCURRENCY_CONFLICT",
                "Concurrency conflict",
                "The VIP plan changed. Reload it before retrying.")),
            _ => NotFound(Problem(
                StatusCodes.Status404NotFound,
                "RESOURCE_NOT_FOUND",
                "Resource not found",
                "The active VIP plan is unavailable.")),
        };
    }

    private ApiErrorResponse Problem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>()
        .Create(HttpContext, status, errorCode, title, detail);
}

public sealed class UpdateVipPlanPriceRequest
{
    [Range(1, long.MaxValue)]
    public long PriceVnd { get; init; }

    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}
