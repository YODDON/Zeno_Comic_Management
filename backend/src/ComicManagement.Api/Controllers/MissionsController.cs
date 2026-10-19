using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Serialization;
using ComicManagement.Missions.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/missions")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class MissionsController(MissionQueryHandler handler) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MissionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<MissionResponse>>> List(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.ListAsync(CurrentUserId(), cancellationToken);
        return result.Outcome == MissionQueryOutcome.Succeeded
            ? Ok(result.Missions!.Select(MissionResponse.FromState).ToArray())
            : BadRequest(ValidationProblem("The authenticated user identifier is invalid."));
    }

    [HttpGet("daily-limit")]
    [ProducesResponseType<MissionDailyLimitState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MissionDailyLimitState>> GetDailyLimit(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.GetDailyLimitAsync(CurrentUserId(), cancellationToken);
        return result.Outcome == MissionQueryOutcome.Succeeded
            ? Ok(result.DailyLimit)
            : BadRequest(ValidationProblem("The authenticated user identifier is invalid."));
    }

    [HttpGet("rewards")]
    [ProducesResponseType<MissionRewardPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MissionRewardPageResponse>> ListRewards(
        [FromQuery, StringLength(MissionQueryHandler.CursorLength,
            MinimumLength = MissionQueryHandler.CursorLength)] string? cursor,
        [FromQuery, Range(1, MissionQueryHandler.MaximumPageSize)] int limit =
            MissionQueryHandler.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.ListRewardsAsync(
            CurrentUserId(), cursor, limit, cancellationToken);
        return result.Outcome == MissionQueryOutcome.Succeeded
            ? Ok(MissionRewardPageResponse.FromState(result.Page!))
            : BadRequest(ValidationProblem("Cursor or limit is invalid."));
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ApiErrorResponse ValidationProblem(string detail) =>
        HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>().Create(
            HttpContext,
            StatusCodes.Status400BadRequest,
            "VALIDATION_FAILED",
            "Validation failed",
            detail);
}

public sealed record MissionResponse(
    Guid MissionId,
    Guid RevisionId,
    string Code,
    string Name,
    string Description,
    string ActionType,
    string CycleType,
    string PeriodKey,
    int TargetCount,
    int ProgressCount,
    long RewardBonusCoins,
    MissionProgressStatusContract ProgressStatus,
    MissionRewardStatusContract RewardStatus,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveUntil)
{
    internal static MissionResponse FromState(MissionState state) => new(
        state.MissionId,
        state.RevisionId,
        state.Code,
        state.Name,
        state.Description,
        state.ActionType,
        state.CycleType,
        state.PeriodKey,
        state.TargetCount,
        state.ProgressCount,
        state.RewardBonusCoins,
        Enum.Parse<MissionProgressStatusContract>(state.ProgressStatus, false),
        Enum.Parse<MissionRewardStatusContract>(state.RewardStatus, false),
        state.EffectiveFrom,
        state.EffectiveUntil);
}

public sealed record MissionRewardResponse(
    Guid RewardGrantId,
    Guid MissionId,
    Guid RevisionId,
    string MissionName,
    MissionRewardOutcomeContract Outcome,
    long RewardBonusCoins,
    long GrantedBonusCoins,
    DateOnly LocalDate,
    DateTimeOffset DecidedAt)
{
    internal static MissionRewardResponse FromState(MissionRewardState state) => new(
        state.RewardGrantId,
        state.MissionId,
        state.RevisionId,
        state.MissionName,
        Enum.Parse<MissionRewardOutcomeContract>(state.Outcome, false),
        state.RewardBonusCoins,
        state.GrantedBonusCoins,
        state.LocalDate,
        state.DecidedAt);
}

public sealed record MissionRewardPageResponse(
    IReadOnlyList<MissionRewardResponse> Items,
    string? NextCursor)
{
    internal static MissionRewardPageResponse FromState(MissionRewardPage state) => new(
        state.Items.Select(MissionRewardResponse.FromState).ToArray(),
        state.NextCursor);
}

[JsonConverter(typeof(StringOnlyEnumConverter<MissionProgressStatusContract>))]
public enum MissionProgressStatusContract
{
    InProgress = 0,
    Completed = 1,
}

[JsonConverter(typeof(StringOnlyEnumConverter<MissionRewardStatusContract>))]
public enum MissionRewardStatusContract
{
    Pending = 0,
    Granted = 1,
    DeniedDailyCap = 2,
}

[JsonConverter(typeof(StringOnlyEnumConverter<MissionRewardOutcomeContract>))]
public enum MissionRewardOutcomeContract
{
    Granted = 0,
    DeniedDailyCap = 1,
}
