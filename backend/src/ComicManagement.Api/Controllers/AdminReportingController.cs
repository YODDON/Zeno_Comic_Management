using System.ComponentModel.DataAnnotations;
using ComicManagement.Administration.Reporting;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Shared.Time;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminReportingController(
    AdministrationReportingHandler reporting,
    IClock clock) : ControllerBase
{
    [HttpGet("dashboard")]
    [ProducesResponseType<AdminDashboard>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminDashboard>> Dashboard(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await reporting.GetDashboardAsync(
            from,
            to,
            AdministrationReportingHandler.GetVietnamToday(clock.UtcNow),
            cancellationToken);
        return result.Outcome == AdministrationReportingOutcome.Succeeded && result.Value is not null
            ? Ok(result.Value)
            : BadRequest(ValidationProblem(
                "The date range must be ordered and contain at most 366 Vietnam calendar days."));
    }

    [HttpGet("audit")]
    [ProducesResponseType<AdminAuditPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminAuditPage>> Audit(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] Guid? actorId,
        [FromQuery, StringLength(AdministrationReportingHandler.MaximumFilterLength)] string? action,
        [FromQuery, StringLength(AdministrationReportingHandler.MaximumFilterLength)] string? targetType,
        [FromQuery] Guid? targetId,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, AdministrationReportingHandler.MaximumPageSize)] int pageSize =
            AdministrationReportingHandler.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await reporting.ListAuditAsync(
            from,
            to,
            AdministrationReportingHandler.GetVietnamToday(clock.UtcNow),
            actorId,
            action,
            targetType,
            targetId,
            page,
            pageSize,
            cancellationToken);
        return result.Outcome == AdministrationReportingOutcome.Succeeded && result.Value is not null
            ? Ok(result.Value)
            : BadRequest(ValidationProblem(
                "The audit filters, date range, or pagination values are invalid."));
    }

    private ApiErrorResponse ValidationProblem(string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>().Create(
        HttpContext,
        StatusCodes.Status400BadRequest,
        "VALIDATION_FAILED",
        "Validation failed",
        detail);
}
