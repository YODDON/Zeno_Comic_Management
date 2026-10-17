using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Reading;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Shared.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/chapters")]
public sealed class ChapterAccessController(
    ChapterAccessHandler accessHandler,
    ChapterUnlockQuoteHandler quoteHandler,
    ChapterUnlockHandler unlockHandler,
    ICurrentUser currentUser) : ControllerBase
{
    [Authorize(Policy = AuthorizationPolicies.User)]
    [HttpPost("{chapterId:guid}/unlock")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<UnlockChapterResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<UnlockChapterResponse>> Unlock(
        Guid chapterId,
        UnlockChapterRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(
            200,
            MinimumLength = 1)]
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var userId = currentUser.UserId ?? throw new InvalidOperationException(
            "An authenticated user identifier is required.");
        var result = await unlockHandler.UnlockAsync(
            userId,
            chapterId,
            request.QuoteId,
            idempotencyKey,
            cancellationToken);
        if (result.Outcome == UnlockChapterOutcome.Succeeded)
        {
            var state = result.State ?? throw new InvalidOperationException(
                "A successful unlock requires state.");
            return Ok(new UnlockChapterResponse(
                state.ChapterId,
                UnlockChapterAccessTypeResponse.CoinUnlocked,
                new UnlockSpentResponse(
                    state.BonusCoins,
                    state.PaidCoins,
                    checked(state.BonusCoins + state.PaidCoins)),
                new UnlockWalletAfterResponse(
                    state.PaidAvailableAfter,
                    state.BonusAvailableAfter),
                state.AlreadyUnlocked));
        }

        return result.Outcome switch
        {
            UnlockChapterOutcome.ValidationFailed => BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "The unlock request or Idempotency-Key is invalid.")),
            UnlockChapterOutcome.NotFound => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "CHAPTER_UNAVAILABLE",
                "Chapter unavailable",
                "The requested chapter or quote is unavailable.")),
            UnlockChapterOutcome.QuoteExpired => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "QUOTE_EXPIRED",
                "Quote expired",
                "Create a new unlock quote before retrying.")),
            UnlockChapterOutcome.PriceChanged => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "PRICE_CHANGED",
                "Price changed",
                "Create a new unlock quote with the current price.")),
            UnlockChapterOutcome.UnlockNotRequired => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "ALREADY_UNLOCKED",
                "Unlock not required",
                "The chapter is already accessible without a Coin unlock.")),
            UnlockChapterOutcome.IdempotencyConflict => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "IDEMPOTENCY_CONFLICT",
                "Idempotency conflict",
                "The Idempotency-Key was already used with a different request.")),
            UnlockChapterOutcome.InvariantViolation => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "CONCURRENCY_CONFLICT",
                "Financial state conflict",
                "The Wallet state could not be reconciled safely.")),
            UnlockChapterOutcome.InsufficientCoins => UnprocessableEntity(CreateProblem(
                StatusCodes.Status422UnprocessableEntity,
                "INSUFFICIENT_COINS",
                "Insufficient Coins",
                "The Wallet does not contain enough available Coins.")),
            _ => throw new InvalidOperationException("Unknown unlock outcome."),
        };
    }

    [Authorize(Policy = AuthorizationPolicies.User)]
    [EnableRateLimiting(ReadingRateLimitConstants.UnlockQuoteCreatePolicy)]
    [HttpPost("{chapterId:guid}/unlock-quotes")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ChapterUnlockQuoteResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ChapterUnlockQuoteResponse>> CreateUnlockQuote(
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException(
            "An authenticated user identifier is required.");
        var result = await quoteHandler.CreateAsync(userId, chapterId, cancellationToken);
        return result.Outcome switch
        {
            ChapterUnlockQuoteOutcome.Succeeded => StatusCode(
                StatusCodes.Status201Created,
                ToResponse(result.Quote ?? throw new InvalidOperationException(
                    "A successful quote command requires a quote."))),
            ChapterUnlockQuoteOutcome.NotFound => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "CHAPTER_UNAVAILABLE",
                "Chapter unavailable",
                "The requested chapter is unavailable.")),
            ChapterUnlockQuoteOutcome.UnlockNotRequired => Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "ALREADY_UNLOCKED",
                "Unlock not required",
                "The chapter is already accessible to this account.")),
            _ => throw new InvalidOperationException("Unknown unlock quote outcome."),
        };
    }

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

    [HttpGet("{chapterId:guid}/pages/{pageOrder:int:min(1)}/content")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [Produces("image/avif", "image/gif", "image/jpeg", "image/png", "image/webp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPageContent(
        Guid chapterId,
        int pageOrder,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";

        var result = await accessHandler.GetPageContentAsync(
            chapterId,
            pageOrder,
            currentUser.UserId,
            cancellationToken);
        return result.Outcome switch
        {
            ChapterManifestOutcome.Succeeded => File(
                (result.Content ?? throw new InvalidOperationException(
                    "A successful page query requires content.")).Bytes,
                result.Content.ContentType,
                enableRangeProcessing: false),
            ChapterManifestOutcome.NotFound => NotFound(CreateProblem(
                StatusCodes.Status404NotFound,
                "CHAPTER_UNAVAILABLE",
                "Chapter unavailable",
                "The requested chapter page is unavailable.")),
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
            _ => throw new InvalidOperationException("Unknown page content outcome."),
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

    private static ChapterUnlockQuoteResponse ToResponse(ChapterUnlockQuoteState quote) => new(
        quote.QuoteId,
        quote.ChapterId,
        quote.BasePriceCoins,
        quote.VipDiscountApplied,
        quote.FinalPriceCoins,
        quote.BonusToSpend,
        quote.PaidToSpend,
        quote.ExpiresAt,
        quote.PriceRevision);

    private ChapterManifestResponse ToResponse(ChapterManifest manifest) => new(
        manifest.ChapterId,
        manifest.Pages.Select(page => new ChapterManifestPageResponse(
                page.Order,
                page.Width,
                page.Height,
                Url.Action(
                    nameof(GetPageContent),
                    values: new
                    {
                        chapterId = manifest.ChapterId,
                        pageOrder = page.Order,
                    }) ?? throw new InvalidOperationException(
                        "The chapter page content route could not be generated.")))
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

public sealed record ChapterUnlockQuoteResponse(
    Guid QuoteId,
    Guid ChapterId,
    [property: Range(1, long.MaxValue)]
    long BasePriceCoins,
    bool VipDiscountApplied,
    [property: Range(1, long.MaxValue)]
    long FinalPriceCoins,
    [property: Range(0, long.MaxValue)]
    long BonusToSpend,
    [property: Range(0, long.MaxValue)]
    long PaidToSpend,
    DateTimeOffset ExpiresAt,
    [property: StringLength(64, MinimumLength = 64)]
    [property: RegularExpression("^[0-9a-f]{64}$")]
    string PriceRevision);

public sealed class UnlockChapterRequest
{
    [Required]
    public Guid QuoteId { get; init; }
}

public sealed record UnlockSpentResponse(
    [property: Range(0, long.MaxValue)] long BonusCoins,
    [property: Range(0, long.MaxValue)] long PaidCoins,
    [property: Range(1, long.MaxValue)] long TotalCoins);

public sealed record UnlockWalletAfterResponse(
    [property: Range(0, long.MaxValue)] long PaidAvailable,
    [property: Range(0, long.MaxValue)] long BonusAvailable);

public sealed record UnlockChapterResponse(
    Guid ChapterId,
    UnlockChapterAccessTypeResponse AccessType,
    UnlockSpentResponse Spent,
    UnlockWalletAfterResponse WalletAfter,
    bool AlreadyUnlocked);

[JsonConverter(typeof(JsonStringEnumConverter<UnlockChapterAccessTypeResponse>))]
public enum UnlockChapterAccessTypeResponse
{
    CoinUnlocked = 0,
}

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
