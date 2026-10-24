using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Interaction;
using ComicManagement.Interaction.Comments;
using ComicManagement.Shared.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CommentsController(CommentHandler handler, ICurrentUser currentUser) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("api/v1/comics/{comicId:guid}/comments")]
    [ProducesResponseType<CommentPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<ActionResult<CommentPageResponse>> ComicComments(Guid comicId, [FromQuery, Range(1, CommentHandler.MaximumPage)] int page = 1, CancellationToken token = default) =>
        Page(handler.ListTargetAsync(CommentTargetTypes.Comic, comicId, page, token));

    [AllowAnonymous]
    [HttpGet("api/v1/chapters/{chapterId:guid}/comments")]
    [ProducesResponseType<CommentPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<ActionResult<CommentPageResponse>> ChapterComments(Guid chapterId, [FromQuery, Range(1, CommentHandler.MaximumPage)] int page = 1, CancellationToken token = default) =>
        Page(handler.ListTargetAsync(CommentTargetTypes.Chapter, chapterId, page, token));

    [AllowAnonymous]
    [HttpGet("api/v1/comments/{commentId:guid}/replies")]
    [ProducesResponseType<CommentPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<ActionResult<CommentPageResponse>> Replies(Guid commentId, [FromQuery, Range(1, CommentHandler.MaximumPage)] int page = 1, CancellationToken token = default) =>
        Page(handler.ListRepliesAsync(commentId, page, token));

    [Authorize(Policy = AuthorizationPolicies.User)]
    [EnableRateLimiting(InteractionRateLimitConstants.CommentMutationPolicy)]
    [HttpPost("api/v1/comments")]
    [ProducesResponseType<CommentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CommentResponse>> Create(CreateCommentRequest request, CancellationToken token) =>
        ToResponse(await handler.CreateAsync(RequireUser(), request.ComicId, request.ChapterId, request.ParentId, request.Body, token), true);

    [Authorize(Policy = AuthorizationPolicies.User)]
    [EnableRateLimiting(InteractionRateLimitConstants.CommentMutationPolicy)]
    [HttpPatch("api/v1/comments/{commentId:guid}")]
    [ProducesResponseType<CommentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CommentResponse>> Edit(Guid commentId, EditCommentRequest request, CancellationToken token) =>
        ToResponse(await handler.EditAsync(commentId, RequireUser(), request.Body, request.ExpectedVersion, token), false);

    [Authorize(Policy = AuthorizationPolicies.User)]
    [EnableRateLimiting(InteractionRateLimitConstants.CommentMutationPolicy)]
    [HttpDelete("api/v1/comments/{commentId:guid}")]
    [ProducesResponseType<CommentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CommentResponse>> Delete(Guid commentId, DeleteCommentRequest request, CancellationToken token) =>
        ToResponse(await handler.DeleteAsync(commentId, RequireUser(), request.ExpectedVersion, token), false);

    private async Task<ActionResult<CommentPageResponse>> Page(Task<CommentPageResult> operation)
    {
        var result = await operation;
        if (result.Outcome == CommentOutcome.Succeeded) return Ok(ToResponse(result.Page!));
        return result.Outcome == CommentOutcome.ValidationFailed
            ? BadRequest(Problem(400, "VALIDATION_FAILED", "Validation failed", "The target and page must be valid."))
            : NotFound(Problem(404, "RESOURCE_NOT_FOUND", "Not found", "The requested comments are unavailable."));
    }

    private ActionResult<CommentResponse> ToResponse(CommentResult result, bool created) => result.Outcome switch
    {
        CommentOutcome.Succeeded when created => Created($"/api/v1/comments/{result.Comment!.Id}", ToResponse(result.Comment)),
        CommentOutcome.Succeeded => Ok(ToResponse(result.Comment!)),
        CommentOutcome.ValidationFailed => BadRequest(Problem(400, "VALIDATION_FAILED", "Validation failed", "The comment request is invalid.")),
        CommentOutcome.TargetUnavailable => NotFound(Problem(404, "RESOURCE_NOT_FOUND", "Not found", "The comment target is unavailable.")),
        CommentOutcome.NotFound => NotFound(Problem(404, "RESOURCE_NOT_FOUND", "Not found", "The comment was not found.")),
        CommentOutcome.Forbidden => StatusCode(403, Problem(403, "FORBIDDEN", "Forbidden", "You cannot modify this comment.")),
        CommentOutcome.InvalidParent => UnprocessableEntity(Problem(422, "COMMENT_PARENT_INVALID", "Invalid parent", "Replies require a visible root comment on the same target.")),
        CommentOutcome.ConcurrencyConflict => Conflict(Problem(409, "CONCURRENCY_CONFLICT", "Conflict", "The comment changed; reload and try again.")),
        CommentOutcome.InvalidState => Conflict(Problem(409, "COMMENT_STATE_CONFLICT", "Conflict", "The comment state does not allow this operation.")),
        _ => throw new InvalidOperationException("Unknown comment outcome."),
    };

    private Guid RequireUser() => currentUser.UserId is { } id && id != Guid.Empty ? id : throw new InvalidOperationException("Authenticated user ID required.");
    private ApiErrorResponse Problem(int status, string code, string title, string detail) => HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>().Create(HttpContext, status, code, title, detail);
    internal static CommentResponse ToResponse(CommentItem item) => new(item.Id, item.UserId, item.DisplayName, item.AvatarUrl, item.ComicId, item.ChapterId, item.ParentId, item.Body, item.Status, item.CreatedAt, item.UpdatedAt, item.Version, item.ReplyCount);
    private static CommentPageResponse ToResponse(CommentPage page) => new(page.Items.Select(ToResponse).ToArray(), page.Page, page.PageSize, page.TotalItems, page.TotalPages);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateCommentRequest
{
    public Guid? ComicId { get; init; }
    public Guid? ChapterId { get; init; }
    public Guid? ParentId { get; init; }
    [Required, StringLength(Comment.MaximumBodyLength, MinimumLength = 1)]
    public string Body { get; init; } = null!;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EditCommentRequest
{
    [Required, StringLength(Comment.MaximumBodyLength, MinimumLength = 1)]
    public string Body { get; init; } = null!;
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DeleteCommentRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}
public sealed record CommentResponse(Guid Id, Guid UserId, string DisplayName, string? AvatarUrl, Guid? ComicId, Guid? ChapterId, Guid? ParentId, string Body, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version, int ReplyCount);
public sealed record CommentPageResponse(IReadOnlyList<CommentResponse> Items, int Page, int PageSize, int TotalItems, int TotalPages);
