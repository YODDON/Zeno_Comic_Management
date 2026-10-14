using System.ComponentModel.DataAnnotations;
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
    [HttpPost("comics/{comicId:guid}/follow")]
    [ProducesResponseType<FollowComicResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<FollowComicResponse>(StatusCodes.Status201Created)]
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
        var response = new FollowComicResponse(
            state.ComicId,
            Followed: true,
            state.FollowedAt,
            state.Created);
        return state.Created
            ? StatusCode(StatusCodes.Status201Created, response)
            : Ok(response);
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

    [HttpGet("reading/continue")]
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

    [HttpGet("library/follows")]
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
        item.Availability,
        item.Slug,
        item.Title,
        item.CoverAssetId,
        item.SeriesStatus,
        item.ChapterId,
        item.ChapterLabel,
        item.PageOrder,
        item.PageOffsetRatio,
        item.LastReadAt,
        item.AccessType,
        item.CanRead);

    private static FollowedComicItemResponse ToResponse(FollowedComicItem item) => new(
        item.ComicId,
        item.Availability,
        item.Slug,
        item.Title,
        item.CoverAssetId,
        item.SeriesStatus,
        item.FollowedAt);
}

public sealed record FollowComicResponse(
    Guid ComicId,
    bool Followed,
    DateTimeOffset FollowedAt,
    bool Created);

public sealed record ContinueReadingPageResponse(
    IReadOnlyList<ContinueReadingItemResponse> Items,
    string? NextCursor);

public sealed record ContinueReadingItemResponse(
    Guid ComicId,
    string Availability,
    string? Slug,
    string? Title,
    string? CoverAssetId,
    string? SeriesStatus,
    Guid? ChapterId,
    string? ChapterLabel,
    int? PageOrder,
    decimal? PageOffsetRatio,
    DateTimeOffset LastReadAt,
    string? AccessType,
    bool CanRead);

public sealed record FollowedComicPageResponse(
    IReadOnlyList<FollowedComicItemResponse> Items,
    string? NextCursor);

public sealed record FollowedComicItemResponse(
    Guid ComicId,
    string Availability,
    string? Slug,
    string? Title,
    string? CoverAssetId,
    string? SeriesStatus,
    DateTimeOffset FollowedAt);
