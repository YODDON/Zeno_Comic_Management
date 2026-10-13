using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Reading.Access;
using ComicManagement.Shared.Identity;
using ComicManagement.Shared.Time;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/chapters")]
public sealed class ChapterAccessController(
    ChapterAccessHandler accessHandler,
    ICurrentUser currentUser,
    IClock clock) : ControllerBase
{
    [HttpGet("{chapterId:guid}/access")]
    [ProducesResponseType<ChapterAccessResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChapterAccessResponse>> GetAccess(
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var result = await accessHandler.GetAccessAsync(
            chapterId,
            currentUser.UserId,
            cancellationToken);
        if (result.Outcome == ChapterAccessOutcome.NotFound || result.Access is null)
        {
            return NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "CHAPTER_UNAVAILABLE",
                "Chapter unavailable",
                "The requested chapter is unavailable."));
        }

        return Ok(ToResponse(result.Access));
    }

    [HttpGet("{chapterId:guid}/pages")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ChapterManifestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ChapterManifestResponse>> GetPages(
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var result = await accessHandler.GetManifestAsync(
            chapterId,
            currentUser.UserId,
            clock.UtcNow,
            cancellationToken);
        return result.Outcome switch
        {
            ChapterManifestOutcome.Succeeded => Ok(ToResponse(
                result.Manifest ?? throw new InvalidOperationException(
                    "A successful manifest query requires a manifest."))),
            ChapterManifestOutcome.NotFound => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "CHAPTER_UNAVAILABLE",
                "Chapter unavailable",
                "The requested chapter is unavailable.")),
            ChapterManifestOutcome.AuthenticationRequired => Unauthorized(CreateProblem(
                StatusCodes.Status401Unauthorized,
                "AUTH_REQUIRED",
                "Authentication required",
                "Sign in with Google before accessing this locked chapter.")),
            ChapterManifestOutcome.Forbidden => StatusCode(
                StatusCodes.Status403Forbidden,
                CreateProblem(
                    StatusCodes.Status403Forbidden,
                    "FORBIDDEN",
                    "Forbidden",
                    "This account does not have access to the chapter.")),
            ChapterManifestOutcome.ProtectedDeliveryUnavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                CreateProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    "PROVIDER_UNAVAILABLE",
                    "Protected delivery unavailable",
                    "Protected chapter delivery is not currently available.")),
            ChapterManifestOutcome.ProviderUnavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                CreateProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    "PROVIDER_UNAVAILABLE",
                    "Media unavailable",
                    "The chapter media could not be delivered.")),
            _ => throw new InvalidOperationException("Unknown manifest outcome."),
        };
    }

    private ApiErrorResponse CreateProblem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>()
        .Create(HttpContext, status, errorCode, title, detail);

    private static ChapterAccessResponse ToResponse(ChapterAccessDecision access) => new(
        access.ChapterId,
        Enum.Parse<ChapterAccessTypeResponse>(access.AccessType),
        access.CanRead,
        access.PriceCoins,
        access.NeedsLogin);

    private static ChapterManifestResponse ToResponse(ChapterManifest manifest) => new(
        manifest.ChapterId,
        manifest.Pages.Select(page => new ChapterManifestPageResponse(
                page.Order,
                page.Width,
                page.Height,
                page.DeliveryUrl))
            .ToArray(),
        manifest.ManifestIssuedAt,
        manifest.RefreshHint);
}

[JsonConverter(typeof(JsonStringEnumConverter<ChapterAccessTypeResponse>))]
public enum ChapterAccessTypeResponse
{
    Free = 0,
    VipIncluded = 1,
    CoinUnlocked = 2,
    Locked = 3,
    Unavailable = 4,
}

public sealed record ChapterAccessResponse(
    Guid ChapterId,
    ChapterAccessTypeResponse AccessType,
    bool CanRead,
    long PriceCoins,
    bool NeedsLogin);

public sealed record ChapterManifestPageResponse(
    int Order,
    int Width,
    int Height,
    string DeliveryUrl);

public sealed record ChapterManifestResponse(
    Guid ChapterId,
    IReadOnlyList<ChapterManifestPageResponse> Pages,
    DateTimeOffset ManifestIssuedAt,
    string RefreshHint);
