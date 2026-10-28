using System.ComponentModel.DataAnnotations;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Missions.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin/mission-rewards")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminMissionRewardsController(MissionRewardAdministrationHandler handler)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AdminMissionRewardPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminMissionRewardPage>> List(
        [FromQuery] string? outcome,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, MissionRewardAdministrationHandler.MaximumPageSize)] int pageSize =
            MissionRewardAdministrationHandler.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.ListAsync(outcome, page, pageSize, cancellationToken);
        return result.Outcome == MissionRewardAdministrationOutcome.Succeeded && result.Page is not null
            ? Ok(result.Page)
            : BadRequest(HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>().Create(
                HttpContext,
                400,
                "VALIDATION_FAILED",
                "Validation failed",
                "The Mission reward list criteria are invalid."));
    }
}
