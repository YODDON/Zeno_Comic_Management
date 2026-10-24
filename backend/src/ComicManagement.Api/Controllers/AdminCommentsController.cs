using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Interaction;
using ComicManagement.Interaction.Comments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("api/v1/admin/comments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminCommentsController(CommentHandler handler) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AdminCommentPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminCommentPageResponse>> List([FromQuery] string? status, [FromQuery, Range(1, CommentHandler.MaximumPage)] int page = 1, CancellationToken token = default)
    {
        var result = await handler.ListForModerationAsync(status, page, token);
        return result.Outcome == CommentOutcome.Succeeded
            ? Ok(new AdminCommentPageResponse(result.Page!.Items.Select(ToAdminResponse).ToArray(), result.Page.Page, result.Page.PageSize, result.Page.TotalItems, result.Page.TotalPages))
            : BadRequest(Problem(400, "VALIDATION_FAILED", "Validation failed", "Status or page is invalid."));
    }

    [HttpPost("{commentId:guid}/hide")]
    [EnableRateLimiting(InteractionRateLimitConstants.CommentMutationPolicy)]
    [ProducesResponseType<CommentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public Task<ActionResult<CommentResponse>> Hide(Guid commentId, ModerateCommentRequest request, CancellationToken token) => Moderate(commentId, CommentModerationActions.Hide, request, token);

    [HttpPost("{commentId:guid}/delete")]
    [EnableRateLimiting(InteractionRateLimitConstants.CommentMutationPolicy)]
    [ProducesResponseType<CommentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public Task<ActionResult<CommentResponse>> Delete(Guid commentId, ModerateCommentRequest request, CancellationToken token) => Moderate(commentId, CommentModerationActions.Delete, request, token);

    [HttpPost("{commentId:guid}/restore")]
    [EnableRateLimiting(InteractionRateLimitConstants.CommentMutationPolicy)]
    [ProducesResponseType<CommentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public Task<ActionResult<CommentResponse>> Restore(Guid commentId, ModerateCommentRequest request, CancellationToken token) => Moderate(commentId, CommentModerationActions.Restore, request, token);

    private async Task<ActionResult<CommentResponse>> Moderate(Guid id, string action, ModerateCommentRequest request, CancellationToken token)
    {
        var actor = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var value) ? value : Guid.Empty;
        var result = await handler.ModerateAsync(id, action, request.ExpectedVersion, actor, HttpContext.TraceIdentifier, request.Reason, token);
        return result.Outcome switch
        {
            CommentOutcome.Succeeded => Ok(CommentsController.ToResponse(result.Comment!)),
            CommentOutcome.ValidationFailed => BadRequest(Problem(400, "VALIDATION_FAILED", "Validation failed", "The moderation request is invalid.")),
            CommentOutcome.NotFound => NotFound(Problem(404, "RESOURCE_NOT_FOUND", "Not found", "The comment was not found.")),
            CommentOutcome.ConcurrencyConflict => Conflict(Problem(409, "CONCURRENCY_CONFLICT", "Conflict", "The comment changed; reload and try again.")),
            CommentOutcome.InvalidState => Conflict(Problem(409, "COMMENT_STATE_CONFLICT", "Conflict", "The moderation transition is not allowed.")),
            _ => throw new InvalidOperationException("Unknown moderation outcome."),
        };
    }

    private ApiErrorResponse Problem(int status, string code, string title, string detail) => HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>().Create(HttpContext, status, code, title, detail);
    private static AdminCommentResponse ToAdminResponse(CommentItem item) => new(
        item.Id, item.UserId, item.DisplayName, item.AvatarUrl, item.ComicId, item.ChapterId,
        item.ParentId, item.Body, item.Status, item.CreatedAt, item.UpdatedAt, item.Version,
        item.ReplyCount, item.TargetTitle!, item.TargetSlug, item.ParentBody,
        item.ParentAuthorDisplayName);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ModerateCommentRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
    [Required, StringLength(500, MinimumLength = 1)]
    public string Reason { get; init; } = null!;
}

public sealed record AdminCommentResponse(
    Guid Id, Guid UserId, string DisplayName, string? AvatarUrl, Guid? ComicId,
    Guid? ChapterId, Guid? ParentId, string Body, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version, int ReplyCount,
    string TargetTitle, string? TargetSlug, string? ParentBody,
    string? ParentAuthorDisplayName);

public sealed record AdminCommentPageResponse(
    IReadOnlyList<AdminCommentResponse> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);
