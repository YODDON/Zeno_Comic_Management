using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Identity;
using ComicManagement.Identity.Administration;
using ComicManagement.Identity.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminUsersController(UserAdministrationHandler handler) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AdminUserPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminUserPage>> List(
        [FromQuery, StringLength(UserAdministrationHandler.MaximumSearchLength)] string? search,
        [FromQuery] string? status,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, UserAdministrationHandler.MaximumPageSize)] int pageSize =
            UserAdministrationHandler.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.ListAsync(search, status, page, pageSize, cancellationToken);
        return result.Outcome == UserAdministrationOutcome.Succeeded && result.Page is not null
            ? Ok(result.Page)
            : BadRequest(Problem(
                400,
                "VALIDATION_FAILED",
                "Validation failed",
                "The user list criteria are invalid."));
    }

    [HttpGet("{userId:guid}")]
    [ProducesResponseType<AdminUserDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserDetail>> Get(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        return ToDetailResponse(await handler.GetAsync(userId, cancellationToken));
    }

    [HttpPost("{userId:guid}/disable")]
    [EnableRateLimiting(AdminIdentityRateLimitConstants.MutationPolicy)]
    [ProducesResponseType<AdminUserDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public Task<ActionResult<AdminUserDetail>> Disable(
        Guid userId,
        ChangeUserStatusRequest request,
        CancellationToken cancellationToken = default) =>
        ChangeStatus(userId, request, UserStatuses.Disabled, cancellationToken);

    [HttpPost("{userId:guid}/enable")]
    [EnableRateLimiting(AdminIdentityRateLimitConstants.MutationPolicy)]
    [ProducesResponseType<AdminUserDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public Task<ActionResult<AdminUserDetail>> Enable(
        Guid userId,
        ChangeUserStatusRequest request,
        CancellationToken cancellationToken = default) =>
        ChangeStatus(userId, request, UserStatuses.Active, cancellationToken);

    private async Task<ActionResult<AdminUserDetail>> ChangeStatus(
        Guid userId,
        ChangeUserStatusRequest request,
        string status,
        CancellationToken cancellationToken) => ToDetailResponse(
        await handler.ChangeStatusAsync(
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            userId,
            status,
            request.ExpectedVersion,
            HttpContext.TraceIdentifier,
            cancellationToken));

    private ActionResult<AdminUserDetail> ToDetailResponse(UserAdministrationResult result) =>
        result.Outcome switch
        {
            UserAdministrationOutcome.Succeeded when result.Detail is not null => Ok(result.Detail),
            UserAdministrationOutcome.NotFound => NotFound(Problem(
                404,
                "RESOURCE_NOT_FOUND",
                "Resource not found",
                "The requested user was not found.")),
            UserAdministrationOutcome.ConcurrencyConflict => Conflict(Problem(
                409,
                "CONCURRENCY_CONFLICT",
                "Concurrency conflict",
                "The user changed. Reload before retrying.")),
            _ => BadRequest(Problem(
                400,
                "VALIDATION_FAILED",
                "Validation failed",
                "The user status request is invalid.")),
        };

    private ApiErrorResponse Problem(int status, string code, string title, string detail) =>
        HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>()
            .Create(HttpContext, status, code, title, detail);
}

public sealed class ChangeUserStatusRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}
