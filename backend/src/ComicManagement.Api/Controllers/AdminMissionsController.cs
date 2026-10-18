using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Missions;
using ComicManagement.Api.Serialization;
using ComicManagement.Missions.Administration;
using ComicManagement.Missions.Definitions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin/missions")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminMissionsController(MissionAdministrationHandler handler) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MissionDefinitionState>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<MissionDefinitionState>>> List(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.ListAsync(cancellationToken);
        return Ok(result.Definitions ?? []);
    }

    [HttpGet("{missionId:guid}")]
    [ProducesResponseType<MissionDefinitionState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MissionDefinitionState>> Get(
        Guid missionId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.GetAsync(missionId, cancellationToken);
        return result.Outcome == MissionAdministrationOutcome.Succeeded &&
               result.Definition is not null
            ? Ok(result.Definition)
            : NotFound(Problem(
                StatusCodes.Status404NotFound,
                "MISSION_NOT_FOUND",
                "Mission not found",
                "The requested Mission does not exist."));
    }

    [HttpPost]
    [ProducesResponseType<MissionDefinitionState>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(MissionRateLimitConstants.AdminMutationPolicy)]
    public async Task<ActionResult<MissionDefinitionState>> Create(
        CreateMissionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await handler.CreateAsync(
            ActorId(),
            request.Code,
            request.Name,
            request.Description,
            request.ActionType?.ToString(),
            request.CycleType?.ToString(),
            HttpContext.TraceIdentifier,
            cancellationToken);
        if (result.Outcome == MissionAdministrationOutcome.Succeeded &&
            result.Definition is not null)
        {
            return CreatedAtAction(
                nameof(Get),
                new { missionId = result.Definition.Id },
                result.Definition);
        }

        return result.Outcome switch
        {
            MissionAdministrationOutcome.CodeConflict => Conflict(Problem(
                StatusCodes.Status409Conflict,
                "MISSION_CODE_CONFLICT",
                "Mission code conflict",
                "A Mission with the same code already exists.")),
            _ => BadRequest(CreateValidationProblem()),
        };
    }

    [HttpPost("{missionId:guid}/revisions")]
    [ProducesResponseType<MissionRevisionState>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(MissionRateLimitConstants.AdminMutationPolicy)]
    public async Task<ActionResult<MissionRevisionState>> AddRevision(
        Guid missionId,
        AddMissionRevisionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await handler.AddRevisionAsync(
            ActorId(),
            missionId,
            request.TargetCount,
            request.RewardBonusCoins,
            request.RepeatPolicy?.ToString(),
            request.EffectiveFrom,
            request.EffectiveUntil,
            request.ConfigurationSnapshot?.GetRawText(),
            HttpContext.TraceIdentifier,
            cancellationToken);
        return result.Outcome switch
        {
            MissionAdministrationOutcome.Succeeded when result.Revision is not null =>
                StatusCode(StatusCodes.Status201Created, result.Revision),
            MissionAdministrationOutcome.NotFound => NotFound(Problem(
                StatusCodes.Status404NotFound,
                "MISSION_NOT_FOUND",
                "Mission not found",
                "The requested Mission does not exist.")),
            MissionAdministrationOutcome.StateConflict => Conflict(Problem(
                StatusCodes.Status409Conflict,
                "MISSION_STATE_CONFLICT",
                "Mission state conflict",
                "The revision is inconsistent with the Mission action policy.")),
            _ => BadRequest(CreateValidationProblem()),
        };
    }

    [HttpPost("{missionId:guid}/activate")]
    [ProducesResponseType<MissionDefinitionState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(MissionRateLimitConstants.AdminMutationPolicy)]
    public async Task<ActionResult<MissionDefinitionState>> Activate(
        Guid missionId,
        ActivateMissionRevisionRequest request,
        CancellationToken cancellationToken) => ToActivationResponse(await handler.ActivateAsync(
            ActorId(),
            missionId,
            request.RevisionId,
            request.ExpectedVersion,
            HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPost("{missionId:guid}/deactivate")]
    [ProducesResponseType<MissionDefinitionState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [EnableRateLimiting(MissionRateLimitConstants.AdminMutationPolicy)]
    public async Task<ActionResult<MissionDefinitionState>> Deactivate(
        Guid missionId,
        DeactivateMissionRequest request,
        CancellationToken cancellationToken) => ToActivationResponse(await handler.DeactivateAsync(
            ActorId(),
            missionId,
            request.ExpectedVersion,
            HttpContext.TraceIdentifier,
            cancellationToken));

    private ActionResult<MissionDefinitionState> ToActivationResponse(
        MissionAdministrationResult result) => result.Outcome switch
        {
            MissionAdministrationOutcome.Succeeded when result.Definition is not null =>
                Ok(result.Definition),
            MissionAdministrationOutcome.NotFound => NotFound(Problem(
                StatusCodes.Status404NotFound,
                "MISSION_NOT_FOUND",
                "Mission not found",
                "The requested Mission does not exist.")),
            MissionAdministrationOutcome.ConcurrencyConflict => Conflict(Problem(
                StatusCodes.Status409Conflict,
                "CONCURRENCY_CONFLICT",
                "Concurrency conflict",
                "The Mission changed. Reload it before retrying.")),
            MissionAdministrationOutcome.StateConflict => Conflict(Problem(
                StatusCodes.Status409Conflict,
                "MISSION_STATE_CONFLICT",
                "Mission state conflict",
                "The selected revision does not belong to this Mission or violates its policy.")),
            _ => BadRequest(CreateValidationProblem()),
        };

    private Guid ActorId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ApiErrorResponse CreateValidationProblem() => Problem(
        StatusCodes.Status400BadRequest,
        "VALIDATION_FAILED",
        "Validation failed",
        "The Mission request is invalid.");

    private ApiErrorResponse Problem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>()
        .Create(HttpContext, status, errorCode, title, detail);
}

public sealed class CreateMissionRequest
{
    [Required]
    [StringLength(MissionDefinition.MaximumCodeLength, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required]
    [StringLength(MissionDefinition.MaximumNameLength, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(MissionDefinition.MaximumDescriptionLength)]
    public string Description { get; init; } = string.Empty;

    [Required]
    public MissionActionTypeContract? ActionType { get; init; }

    [Required]
    public MissionCycleTypeContract? CycleType { get; init; }
}

public sealed class AddMissionRevisionRequest
{
    [Range(1, int.MaxValue)]
    public int TargetCount { get; init; }

    [Range(1, 300)]
    public long RewardBonusCoins { get; init; }

    [Required]
    public MissionRepeatPolicyContract? RepeatPolicy { get; init; }

    public DateTimeOffset EffectiveFrom { get; init; }

    public DateTimeOffset? EffectiveUntil { get; init; }

    [Required]
    public JsonElement? ConfigurationSnapshot { get; init; }
}

public sealed class ActivateMissionRevisionRequest
{
    public Guid RevisionId { get; init; }

    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}

public sealed class DeactivateMissionRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}

[JsonConverter(typeof(StringOnlyEnumConverter<MissionActionTypeContract>))]
public enum MissionActionTypeContract
{
    DailyLogin = 1,
    ChapterReadQualified = 2,
    ComicFollowed = 3,
    ChapterUnlocked = 4,
    WalletTopUpCompleted = 5,
}

[JsonConverter(typeof(StringOnlyEnumConverter<MissionCycleTypeContract>))]
public enum MissionCycleTypeContract
{
    Fixed = 1,
    Daily = 2,
    Weekly = 3,
    Event = 4,
}

[JsonConverter(typeof(StringOnlyEnumConverter<MissionRepeatPolicyContract>))]
public enum MissionRepeatPolicyContract
{
    OncePerVietnamDate = 1,
    UniqueSubjectPerPeriod = 2,
}
