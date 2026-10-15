using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Security.Claims;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Reading.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize(Policy = AuthorizationPolicies.User)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LibraryController(LibraryHandler handler) : ControllerBase
{
    [HttpPut("comics/{comicId:guid}/follow")]
    [ProducesResponseType<FollowComicResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FollowComicResponse>> Follow(
        Guid comicId,
        CancellationToken cancellationToken)
    {
        var result = await handler.FollowAsync(GetUserId(), comicId, cancellationToken);
        if (result.Outcome == LibraryOutcome.ValidationFailed)
        {
            return BadRequest(ValidationProblem("A valid Comic ID is required."));
        }

        if (result.Outcome == LibraryOutcome.NotFound)
        {
            return NotFound(NotFoundProblem());
        }

        var state = result.State ?? throw new InvalidOperationException(
            "A successful follow command requires state.");
        return Ok(new FollowComicResponse(
            state.ComicId,
            Followed: true,
            state.FollowedAt));
    }

    [HttpDelete("comics/{comicId:guid}/follow")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Unfollow(
        Guid comicId,
        CancellationToken cancellationToken)
    {
        var outcome = await handler.UnfollowAsync(GetUserId(), comicId, cancellationToken);
        return outcome == LibraryOutcome.Succeeded
            ? NoContent()
            : BadRequest(ValidationProblem("A valid Comic ID is required."));
    }

    [HttpGet("comics/{comicId:guid}/follow-status")]
    [ProducesResponseType<FollowStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<FollowStatusResponse>> FollowStatus(
        Guid comicId,
        CancellationToken cancellationToken)
    {
        var result = await handler.GetFollowStatusAsync(
            GetUserId(),
            comicId,
            cancellationToken);
        if (result.Outcome == LibraryOutcome.ValidationFailed)
        {
            return BadRequest(ValidationProblem("A valid Comic ID is required."));
        }

        var state = result.State ?? throw new InvalidOperationException(
            "A successful follow-status query requires state.");
        return Ok(new FollowStatusResponse(
            state.ComicId,
            state.Followed,
            state.FollowedAt));
    }

    [HttpGet("library/continue")]
    [ProducesResponseType<ContinueReadingPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ContinueReadingPageResponse>> ListContinue(
        CancellationToken cancellationToken,
        [FromQuery, StringLength(
            LibraryHandler.CursorLength,
            MinimumLength = LibraryHandler.CursorLength)] string? cursor = null,
        [FromQuery, Range(1, LibraryHandler.MaximumPageSize)] int limit =
            LibraryHandler.DefaultPageSize)
    {
        var result = await handler.ListContinueAsync(
            GetUserId(),
            cursor,
            limit,
            cancellationToken);
        if (result.Outcome == LibraryOutcome.ValidationFailed)
        {
            return BadRequest(ValidationProblem("Cursor or limit is invalid."));
        }

        var page = result.Page ?? throw new InvalidOperationException(
            "A successful Continue query requires a page.");
        return Ok(new ContinueReadingPageResponse(
            page.Items.Select(ToResponse).ToArray(),
            page.NextCursor));
    }

    [HttpGet("library/following")]
    [ProducesResponseType<FollowedComicPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<FollowedComicPageResponse>> ListFollows(
        CancellationToken cancellationToken,
        [FromQuery, StringLength(
            LibraryHandler.CursorLength,
            MinimumLength = LibraryHandler.CursorLength)] string? cursor = null,
        [FromQuery, Range(1, LibraryHandler.MaximumPageSize)] int limit =
            LibraryHandler.DefaultPageSize)
    {
        var result = await handler.ListFollowsAsync(
            GetUserId(),
            cursor,
            limit,
            cancellationToken);
        if (result.Outcome == LibraryOutcome.ValidationFailed)
        {
            return BadRequest(ValidationProblem("Cursor or limit is invalid."));
        }

        var page = result.Page ?? throw new InvalidOperationException(
            "A successful Followed query requires a page.");
        return Ok(new FollowedComicPageResponse(
            page.Items.Select(ToResponse).ToArray(),
            page.NextCursor));
    }

    private Guid GetUserId()
    {
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return userId;
        }

        throw new InvalidOperationException("The authenticated User requires a user ID claim.");
    }

    private ApiErrorResponse ValidationProblem(string detail) => CreateProblem(
        StatusCodes.Status400BadRequest,
        "VALIDATION_FAILED",
        "Validation failed",
        detail);

    private ApiErrorResponse NotFoundProblem() => CreateProblem(
        StatusCodes.Status404NotFound,
        "RESOURCE_NOT_FOUND",
        "Not found",
        "The requested published comic was not found.");

    private ApiErrorResponse CreateProblem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>()
        .Create(HttpContext, status, errorCode, title, detail);

    private static ContinueReadingItemResponse ToResponse(ContinueReadingItem item) => new(
        item.ComicId,
        ToAvailability(item.Availability),
        item.Slug,
        item.Title,
        item.CoverAssetId,
        item.SeriesStatus,
        item.ChapterId,
        item.ChapterLabel,
        item.PageOrder,
        item.PageOffsetRatio,
        item.AcceptedMaxProgressBasisPoints,
        item.LastReadAt,
        item.AccessType,
        item.CanRead);

    private static FollowedComicItemResponse ToResponse(FollowedComicItem item) => new(
        item.ComicId,
        ToAvailability(item.Availability),
        item.Slug,
        item.Title,
        item.CoverAssetId,
        item.SeriesStatus,
        item.FollowedAt);

    private static LibraryAvailabilityResponse ToAvailability(string availability) => availability switch
    {
        LibraryAvailability.Available => LibraryAvailabilityResponse.Available,
        LibraryAvailability.Unavailable => LibraryAvailabilityResponse.Unavailable,
        _ => throw new InvalidOperationException($"Unsupported Library availability '{availability}'."),
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<LibraryAvailabilityResponse>))]
public enum LibraryAvailabilityResponse
{
    Available = 0,
    Unavailable = 1,
}

public sealed record FollowComicResponse(
    Guid ComicId,
    bool Followed,
    DateTimeOffset FollowedAt);

public sealed record FollowStatusResponse(
    Guid ComicId,
    bool Followed,
    DateTimeOffset? FollowedAt);

public sealed record ContinueReadingPageResponse(
    IReadOnlyList<ContinueReadingItemResponse> Items,
    string? NextCursor);

public sealed record ContinueReadingItemResponse(
    Guid ComicId,
    LibraryAvailabilityResponse Availability,
    string? Slug,
    string? Title,
    string? CoverAssetId,
    string? SeriesStatus,
    Guid? ChapterId,
    string? ChapterLabel,
    int? PageOrder,
    [property: Range(0d, 1d)]
    decimal? PageOffsetRatio,
    [property: Range(0, 10_000)]
    int? AcceptedMaxProgressBasisPoints,
    DateTimeOffset LastReadAt,
    string? AccessType,
    bool CanRead);

public sealed record FollowedComicPageResponse(
    IReadOnlyList<FollowedComicItemResponse> Items,
    string? NextCursor);

public sealed record FollowedComicItemResponse(
    Guid ComicId,
    LibraryAvailabilityResponse Availability,
    string? Slug,
    string? Title,
    string? CoverAssetId,
    string? SeriesStatus,
    DateTimeOffset FollowedAt);
