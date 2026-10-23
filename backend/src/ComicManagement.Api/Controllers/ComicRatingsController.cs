using System.ComponentModel.DataAnnotations;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Interaction.Ratings;
using ComicManagement.Shared.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/comics/{comicId:guid}/rating")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ComicRatingsController(
    ComicRatingHandler handler,
    ICurrentUser currentUser) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<ComicRatingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ComicRatingResponse>> Get(
        Guid comicId,
        CancellationToken cancellationToken) =>
        ToResponse(await handler.GetAsync(comicId, currentUser.UserId, cancellationToken));

    [Authorize(Policy = AuthorizationPolicies.User)]
    [HttpPut]
    [ProducesResponseType<ComicRatingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ComicRatingResponse>> Put(
        Guid comicId,
        SetComicRatingRequest request,
        CancellationToken cancellationToken) =>
        ToResponse(await handler.UpsertAsync(
            comicId,
            RequireUserId(),
            request.Stars,
            cancellationToken));

    [Authorize(Policy = AuthorizationPolicies.User)]
    [HttpDelete]
    [ProducesResponseType<ComicRatingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ComicRatingResponse>> Delete(
        Guid comicId,
        CancellationToken cancellationToken) =>
        ToResponse(await handler.DeleteAsync(comicId, RequireUserId(), cancellationToken));

    private ActionResult<ComicRatingResponse> ToResponse(ComicRatingResult result) =>
        result.Outcome switch
        {
            ComicRatingOutcome.Succeeded => Ok(ToResponse(
                result.Summary ?? throw new InvalidOperationException(
                    "A successful rating request requires a summary."))),
            ComicRatingOutcome.ValidationFailed => BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "Comic ID and a star value between 1 and 5 are required.")),
            ComicRatingOutcome.ComicUnavailable => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "RESOURCE_NOT_FOUND",
                "Not found",
                "The requested published comic is unavailable.")),
            _ => throw new InvalidOperationException("Unknown comic rating outcome."),
        };

    private Guid RequireUserId() => currentUser.UserId is { } userId && userId != Guid.Empty
        ? userId
        : throw new InvalidOperationException("An authenticated user ID is required.");

    private ApiErrorResponse CreateProblem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>()
        .Create(HttpContext, status, errorCode, title, detail);

    private static ComicRatingResponse ToResponse(ComicRatingSummary summary) => new(
        summary.AverageStars,
        summary.RatingCount,
        summary.MyRating);
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(
    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class SetComicRatingRequest
{
    [Required]
    [Range(1, 5)]
    public int Stars { get; init; }
}

public sealed record ComicRatingResponse(
    [property: Range(typeof(decimal), "0", "5")]
    decimal AverageStars,
    [property: Range(0, int.MaxValue)]
    int RatingCount,
    [property: Range(1, 5)]
    short? MyRating);
