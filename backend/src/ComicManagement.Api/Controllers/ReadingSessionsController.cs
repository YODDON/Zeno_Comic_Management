using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Reading;
using ComicManagement.Reading.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/reading/sessions")]
[Authorize(Policy = AuthorizationPolicies.User)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReadingSessionsController(
    ReadingSessionHandler sessionHandler) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<CreateReadingSessionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CreateReadingSessionResponse>> Create(
        CreateReadingSessionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sessionHandler.CreateAsync(
            GetUserId(),
            request.ChapterId,
            cancellationToken);
        if (result.Outcome != ReadingSessionCommandOutcome.Succeeded)
        {
            return MapError(result);
        }

        var state = RequireState(result);
        var response = new CreateReadingSessionResponse(
            state.SessionId,
            state.StartedAt,
            state.ExpiresAt,
            new ReadingQualificationPolicyResponse(
                state.PolicyRevision,
                state.MinimumProgressBasisPoints,
                state.MinimumActiveDurationMs),
            state.ManifestRevision,
            state.ManifestPageCount,
            state.Version);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPatch("{id:guid}")]
    [EnableRateLimiting(ReadingRateLimitConstants.ProgressPolicy)]
    [ProducesResponseType<UpdateReadingSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<UpdateReadingSessionResponse>> UpdateProgress(
        Guid id,
        UpdateReadingSessionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sessionHandler.UpdateProgressAsync(
            id,
            GetUserId(),
            request.CurrentPageOrder,
            request.CurrentPageOffsetRatio,
            request.SequenceNumber,
            cancellationToken);
        if (result.Outcome != ReadingSessionCommandOutcome.Succeeded)
        {
            return MapError(result);
        }

        var state = RequireState(result);
        return Ok(new UpdateReadingSessionResponse(
            state.SessionId,
            state.CurrentPageOrder.HasValue && state.CurrentPageOffsetRatio.HasValue
                ? new AcceptedReadingPositionResponse(
                    state.CurrentPageOrder.Value,
                    state.CurrentPageOffsetRatio.Value)
                : null,
            state.AcceptedMaxProgressBasisPoints,
            state.ActiveDurationMs,
            state.ManifestRevision,
            state.ManifestPageCount,
            state.LastAcceptedSequence,
            state.ExpiresAt,
            state.Version));
    }

    [HttpPost("{id:guid}/qualify")]
    [ProducesResponseType<QualifyReadingSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QualifyReadingSessionResponse>> Qualify(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await sessionHandler.QualifyAsync(
            id,
            GetUserId(),
            cancellationToken);
        if (result.Outcome != ReadingSessionCommandOutcome.Succeeded)
        {
            return MapError(result);
        }

        var state = RequireState(result);
        return Ok(new QualifyReadingSessionResponse(
            state.QualifiedAt.HasValue,
            state.QualifiedAt,
            state.QualifiedAt.HasValue ? null : result.ReasonCode,
            state.Version));
    }

    private ActionResult MapError(ReadingSessionCommandResult result) => result.Outcome switch
    {
        ReadingSessionCommandOutcome.ValidationFailed => BadRequest(CreateProblem(
            StatusCodes.Status400BadRequest,
            "VALIDATION_FAILED",
            "Validation failed",
            "The reading session request is invalid.")),
        ReadingSessionCommandOutcome.NotFound => NotFound(CreateProblem(
            StatusCodes.Status404NotFound,
            "READING_SESSION_UNAVAILABLE",
            "Reading session unavailable",
            "The requested reading resource is unavailable.")),
        ReadingSessionCommandOutcome.Forbidden => StatusCode(
            StatusCodes.Status403Forbidden,
            CreateProblem(
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "Forbidden",
                "This account does not have access to the chapter.")),
        ReadingSessionCommandOutcome.SequenceConflict => Conflict(CreateProblem(
            StatusCodes.Status409Conflict,
            "READING_SEQUENCE_CONFLICT",
            "Reading sequence conflict",
            "The heartbeat sequence is stale, has a gap, or does not match its prior payload.")),
        ReadingSessionCommandOutcome.Expired => Conflict(CreateProblem(
            StatusCodes.Status409Conflict,
            "READING_SESSION_EXPIRED",
            "Reading session expired",
            "The reading session has reached its absolute expiry.")),
        _ => throw new InvalidOperationException("Unknown reading session outcome."),
    };

    private Guid GetUserId()
    {
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return userId;
        }

        throw new InvalidOperationException("The authenticated User requires a user ID claim.");
    }

    private ApiErrorResponse CreateProblem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>()
        .Create(HttpContext, status, errorCode, title, detail);

    private static ReadingSessionState RequireState(ReadingSessionCommandResult result) =>
        result.State ?? throw new InvalidOperationException(
            "A successful reading session command requires state.");
}

public sealed record CreateReadingSessionRequest(Guid ChapterId);

public sealed class UpdateReadingSessionRequest
{
    [Range(1, int.MaxValue)]
    public int CurrentPageOrder { get; init; }

    [Range(typeof(decimal), "0", "1")]
    public decimal CurrentPageOffsetRatio { get; init; }

    [Range(1, long.MaxValue)]
    public long SequenceNumber { get; init; }
}

public sealed record ReadingQualificationPolicyResponse(
    string Revision,
    int MinimumProgressBasisPoints,
    long MinimumActiveDurationMs);

public sealed record CreateReadingSessionResponse(
    Guid SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    ReadingQualificationPolicyResponse Policy,
    string ManifestRevision,
    int ManifestPageCount,
    long Version);

public sealed record AcceptedReadingPositionResponse(
    int PageOrder,
    decimal PageOffsetRatio);

public sealed record UpdateReadingSessionResponse(
    Guid SessionId,
    AcceptedReadingPositionResponse? AcceptedPosition,
    int AcceptedMaxProgressBasisPoints,
    long ActiveDurationMs,
    string ManifestRevision,
    int ManifestPageCount,
    long LastAcceptedSequence,
    DateTimeOffset ExpiresAt,
    long Version);

public sealed record QualifyReadingSessionResponse(
    bool Qualified,
    DateTimeOffset? QualifiedAt,
    string? ReasonCode,
    long Version);
