using System.Security.Claims;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Catalog.Publishing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
public sealed class AdminCatalogPublicationController(
    CatalogPublicationHandler publicationHandler) : ControllerBase
{
    [HttpPost("comics/{comicId:guid}/publish")]
    [ProducesResponseType<CatalogPublicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CatalogPublicationResponse>> PublishComic(
        Guid comicId,
        CatalogPublicationCommand request,
        CancellationToken cancellationToken) =>
        Map(await publicationHandler.PublishComicAsync(
            comicId,
            request.ExpectedVersion,
            GetActorId(),
            HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPost("comics/{comicId:guid}/hide")]
    [ProducesResponseType<CatalogPublicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CatalogPublicationResponse>> HideComic(
        Guid comicId,
        CatalogPublicationCommand request,
        CancellationToken cancellationToken) =>
        Map(await publicationHandler.HideComicAsync(
            comicId,
            request.ExpectedVersion,
            GetActorId(),
            HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPatch("comics/{comicId:guid}/slug")]
    [ProducesResponseType<ComicSlugResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ComicSlugResponse>> ChangeComicSlug(
        Guid comicId,
        ChangeComicSlugCommand request,
        CancellationToken cancellationToken) =>
        MapSlug(await publicationHandler.ChangeComicSlugAsync(
            comicId,
            request.Slug,
            request.ExpectedVersion,
            GetActorId(),
            HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPost("chapters/{chapterId:guid}/publish")]
    [ProducesResponseType<CatalogPublicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CatalogPublicationResponse>> PublishChapter(
        Guid chapterId,
        CatalogPublicationCommand request,
        CancellationToken cancellationToken) =>
        Map(await publicationHandler.PublishChapterAsync(
            chapterId,
            request.ExpectedVersion,
            GetActorId(),
            HttpContext.TraceIdentifier,
            cancellationToken));

    [HttpPost("chapters/{chapterId:guid}/hide")]
    [ProducesResponseType<CatalogPublicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CatalogPublicationResponse>> HideChapter(
        Guid chapterId,
        CatalogPublicationCommand request,
        CancellationToken cancellationToken) =>
        Map(await publicationHandler.HideChapterAsync(
            chapterId,
            request.ExpectedVersion,
            GetActorId(),
            HttpContext.TraceIdentifier,
            cancellationToken));

    private ActionResult<ComicSlugResponse> MapSlug(CatalogPublicationResult result)
    {
        if (result.Outcome == CatalogPublicationOutcome.Succeeded)
        {
            var state = result.State ?? throw new InvalidOperationException(
                "A successful slug command requires state.");
            return Ok(new ComicSlugResponse(
                state.Id,
                state.Slug ?? throw new InvalidOperationException(
                    "A successful slug command requires a slug."),
                state.Version));
        }

        return result.Outcome switch
        {
            CatalogPublicationOutcome.ValidationFailed => BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "A valid slug, resource ID and expectedVersion of at least 1 are required.")),
            CatalogPublicationOutcome.NotFound => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "RESOURCE_NOT_FOUND",
                "Not found",
                "The requested catalog resource was not found.")),
            CatalogPublicationOutcome.ConcurrencyConflict => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "CONCURRENCY_CONFLICT",
                "Conflict",
                "The catalog resource changed concurrently. Reload it and try again.")),
            CatalogPublicationOutcome.SlugConflict => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "SLUG_CONFLICT",
                "Slug conflict",
                "The requested comic slug is already in use.")),
            CatalogPublicationOutcome.PublicationPreconditionFailed =>
                UnprocessableEntity(CreateProblem(
                    StatusCodes.Status422UnprocessableEntity,
                    "PUBLICATION_PRECONDITION_FAILED",
                    "Publication precondition failed",
                    "A comic slug cannot change after its first publication.")),
            _ => throw new InvalidOperationException("Unknown catalog publication outcome."),
        };
    }

    private ActionResult<CatalogPublicationResponse> Map(CatalogPublicationResult result)
    {
        return result.Outcome switch
        {
            CatalogPublicationOutcome.Succeeded => Ok(ToResponse(
                result.State ?? throw new InvalidOperationException(
                    "A successful publication command requires state."))),
            CatalogPublicationOutcome.ValidationFailed => BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "A non-empty resource ID and expectedVersion of at least 1 are required.")),
            CatalogPublicationOutcome.NotFound => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "RESOURCE_NOT_FOUND",
                "Not found",
                "The requested catalog resource was not found.")),
            CatalogPublicationOutcome.ConcurrencyConflict => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "CONCURRENCY_CONFLICT",
                "Conflict",
                "The catalog resource changed concurrently. Reload it and try again.")),
            CatalogPublicationOutcome.PublicationPreconditionFailed =>
                UnprocessableEntity(CreateProblem(
                    StatusCodes.Status422UnprocessableEntity,
                    "PUBLICATION_PRECONDITION_FAILED",
                    "Publication precondition failed",
                    "The requested publication state would violate catalog invariants.")),
            CatalogPublicationOutcome.SlugConflict => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "SLUG_CONFLICT",
                "Slug conflict",
                "The requested comic slug is already in use.")),
            _ => throw new InvalidOperationException("Unknown catalog publication outcome."),
        };
    }

    private Guid GetActorId()
    {
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
        {
            return actorId;
        }

        throw new InvalidOperationException("The authenticated Admin requires a user ID claim.");
    }

    private ProblemDetails CreateProblem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
            .GetRequiredService<ApiProblemWriter>()
            .Create(HttpContext, status, errorCode, title, detail);

    private static CatalogPublicationResponse ToResponse(CatalogPublicationState state) => new(
        state.Id,
        state.PublicationStatus,
        state.Version,
        state.FirstPublishedAt);
}

public sealed record CatalogPublicationCommand(long ExpectedVersion);

public sealed record ChangeComicSlugCommand(string Slug, long ExpectedVersion);

public sealed record CatalogPublicationResponse(
    Guid Id,
    string PublicationStatus,
    long Version,
    DateTimeOffset? FirstPublishedAt);

public sealed record ComicSlugResponse(Guid Id, string Slug, long Version);
