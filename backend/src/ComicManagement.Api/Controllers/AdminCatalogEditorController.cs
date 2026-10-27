using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Catalog;
using ComicManagement.Catalog.Administration;
using ComicManagement.Catalog.Featured;
using ComicManagement.Catalog.Media;
using ComicManagement.Reading.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
public sealed class AdminCatalogEditorController(
    CatalogAdministrationHandler handler,
    IFeaturedCatalogStore featured,
    IAdminChapterPageService chapterPages,
    IChapterPageContentProvider pageContent) : ControllerBase
{
    [HttpGet("comics")]
    [ProducesResponseType<AdminCatalogPage<AdminComicItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public Task<AdminCatalogPage<AdminComicItem>> Comics(
        [FromQuery, StringLength(200)] string? search,
        [FromQuery, RegularExpression("^(Draft|Published|Hidden)$")] string? publicationStatus,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 30,
        CancellationToken token = default) =>
        handler.ListComicsAsync(search, publicationStatus, page, pageSize, token);

    [HttpGet("comics/{id:guid}")]
    [ProducesResponseType<AdminComicItem>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminComicItem>> Comic(Guid id, CancellationToken token) =>
        await handler.GetComicAsync(id, token) is { } item ? Ok(item) : NotFound(Problem(404, "RESOURCE_NOT_FOUND"));

    [HttpPost("comics")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<AdminComicItem>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminComicItem>> CreateComic(SaveComicRequest request, CancellationToken token) =>
        Map(await handler.SaveComicAsync(request.ToInput(null), Actor(), HttpContext.TraceIdentifier, token), true);

    [HttpPatch("comics/{id:guid}")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<AdminComicItem>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminComicItem>> UpdateComic(Guid id, SaveComicRequest request, CancellationToken token) =>
        Map(await handler.SaveComicAsync(request.ToInput(id), Actor(), HttpContext.TraceIdentifier, token), false);

    [HttpGet("chapters")]
    [ProducesResponseType<AdminCatalogPage<AdminChapterItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    public Task<AdminCatalogPage<AdminChapterItem>> Chapters(
        [FromQuery] Guid? comicId,
        [FromQuery, StringLength(200)] string? search,
        [FromQuery, RegularExpression("^(Draft|Published|Hidden)$")] string? publicationStatus,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 30,
        CancellationToken token = default) =>
        handler.ListChaptersAsync(comicId, search, publicationStatus, page, pageSize, token);

    [HttpGet("chapters/{id:guid}")]
    [ProducesResponseType<AdminChapterItem>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminChapterItem>> Chapter(Guid id, CancellationToken token) =>
        await handler.GetChapterAsync(id, token) is { } item ? Ok(item) : NotFound(Problem(404, "RESOURCE_NOT_FOUND"));

    [HttpPost("chapters")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<AdminChapterItem>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminChapterItem>> CreateChapter(SaveChapterRequest request, CancellationToken token) =>
        Map(await handler.SaveChapterAsync(request.ToInput(null), Actor(), HttpContext.TraceIdentifier, token), true);

    [HttpPatch("chapters/{id:guid}")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<AdminChapterItem>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminChapterItem>> UpdateChapter(Guid id, SaveChapterRequest request, CancellationToken token) =>
        Map(await handler.SaveChapterAsync(request.ToInput(id), Actor(), HttpContext.TraceIdentifier, token), false);

    [HttpGet("chapters/{chapterId:guid}/pages")]
    [ProducesResponseType<AdminPageManifest>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminPageManifest>> PageManifest(Guid chapterId, CancellationToken token) =>
        await chapterPages.GetAsync(chapterId, token) is { } manifest
            ? Ok(manifest)
            : NotFound(Problem(404, "RESOURCE_NOT_FOUND"));

    [HttpGet("chapters/{chapterId:guid}/pages/{pageId:guid}/content")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [Produces("image/avif", "image/gif", "image/jpeg", "image/png", "image/webp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> PageContent(Guid chapterId, Guid pageId, CancellationToken token)
    {
        var key = await chapterPages.GetAssetKeyAsync(chapterId, pageId, token);
        if (key is null) return NotFound(Problem(404, "RESOURCE_NOT_FOUND"));
        var content = await pageContent.GetAsync(key, token);
        return content is null
            ? StatusCode(503, Problem(503, "PROVIDER_UNAVAILABLE"))
            : File(content.Bytes, content.ContentType, enableRangeProcessing: false);
    }

    [HttpPost("chapters/{chapterId:guid}/pages")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [RequestSizeLimit(25_000_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<AdminPageManifest>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminPageManifest>> AppendPages(
        Guid chapterId, [FromForm] AdminAppendPagesRequest request, CancellationToken token)
    {
        if (request.Files is null || request.Files.Count == 0 || request.Files.Any(x => x.Length == 0))
            return BadRequest(Problem(400, "VALIDATION_FAILED"));
        var streams = request.Files.Select(x => x.OpenReadStream()).ToArray();
        try
        {
            var uploads = request.Files.Select((file, index) =>
                new AdminPageUpload(file.FileName, file.ContentType, streams[index])).ToArray();
            return MapPage(await chapterPages.AppendAsync(chapterId, request.ExpectedVersion, uploads,
                Actor(), HttpContext.TraceIdentifier, token));
        }
        finally
        {
            foreach (var stream in streams) await stream.DisposeAsync();
        }
    }

    [HttpPut("chapters/{chapterId:guid}/pages/order")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<AdminPageManifest>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminPageManifest>> ReorderPages(
        Guid chapterId, ReorderPagesRequest request, CancellationToken token) => MapPage(
        await chapterPages.ReorderAsync(chapterId, request.ExpectedVersion, request.PageIds,
            Actor(), HttpContext.TraceIdentifier, token));

    [HttpPut("chapters/{chapterId:guid}/pages/{pageId:guid}/content")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [RequestSizeLimit(25_000_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<AdminPageManifest>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminPageManifest>> ReplacePage(
        Guid chapterId, Guid pageId, [FromForm] AdminReplacePageRequest request, CancellationToken token)
    {
        if (request.File is null || request.File.Length == 0) return BadRequest(Problem(400, "VALIDATION_FAILED"));
        await using var stream = request.File.OpenReadStream();
        return MapPage(await chapterPages.ReplaceAsync(chapterId, pageId, request.ExpectedVersion,
            new(request.File.FileName, request.File.ContentType, stream), Actor(),
            HttpContext.TraceIdentifier, token));
    }

    [HttpDelete("chapters/{chapterId:guid}/pages/{pageId:guid}")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<AdminPageManifest>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminPageManifest>> RemovePage(
        Guid chapterId, Guid pageId, [FromQuery, Range(1, long.MaxValue)] long expectedVersion,
        CancellationToken token) => MapPage(await chapterPages.RemoveAsync(chapterId, pageId,
            expectedVersion, Actor(), HttpContext.TraceIdentifier, token));

    [HttpGet("taxonomy/{kind}")]
    [ProducesResponseType<IReadOnlyList<CatalogOption>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<CatalogOption>>> Taxonomy(string kind, CancellationToken token) =>
        IsTaxonomyKind(kind)
            ? Ok(await handler.ListTaxonomyAsync(kind, token))
            : BadRequest(Problem(400, "VALIDATION_FAILED"));

    [HttpPost("taxonomy/{kind}")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<CatalogOption>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CatalogOption>> CreateTaxonomy(
        string kind, CreateTaxonomyRequest request, CancellationToken token) =>
        Map(await handler.CreateTaxonomyAsync(kind, request.Name, request.Slug, Actor(),
            HttpContext.TraceIdentifier, token), true);

    [HttpPatch("taxonomy/{kind}/{id:guid}")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<CatalogOption>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CatalogOption>> UpdateTaxonomy(string kind, Guid id,
        CreateTaxonomyRequest request, CancellationToken token) => Map(await handler.UpdateTaxonomyAsync(
            kind,id,request.Name,request.Slug,Actor(),HttpContext.TraceIdentifier,token),false);

    [HttpDelete("taxonomy/{kind}/{id:guid}")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<CatalogOption>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CatalogOption>> DeleteTaxonomy(string kind, Guid id,
        CancellationToken token) => Map(await handler.DeleteTaxonomyAsync(
            kind,id,Actor(),HttpContext.TraceIdentifier,token),false);

    [HttpGet("featured")]
    [ProducesResponseType<FeaturedAdminState>(StatusCodes.Status200OK)]
    public Task<FeaturedAdminState> Featured(CancellationToken token) => featured.GetAdminAsync(token);

    [HttpPut("featured")]
    [EnableRateLimiting(CatalogRateLimitConstants.AdminMutationPolicy)]
    [ProducesResponseType<FeaturedAdminState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<FeaturedAdminState>> SaveFeatured(
        SaveFeaturedRequest request, CancellationToken token)
    {
        var result = await featured.SaveAsync(request.ComicIds, request.ExpectedVersion, Actor(),
            HttpContext.TraceIdentifier, DateTimeOffset.UtcNow, token);
        return result.Outcome switch
        {
            FeaturedSaveOutcome.Succeeded => Ok(result.State),
            FeaturedSaveOutcome.ValidationFailed => BadRequest(Problem(400, "VALIDATION_FAILED")),
            FeaturedSaveOutcome.Conflict => Conflict(Problem(409, "CONCURRENCY_CONFLICT")),
            FeaturedSaveOutcome.Unpublished => UnprocessableEntity(Problem(422, "FEATURED_PRECONDITION_FAILED")),
            _ => NotFound(Problem(404, "RESOURCE_NOT_FOUND")),
        };
    }

    private ActionResult<T> Map<T>(CatalogAdministrationResult<T> result, bool created) => result.Outcome switch
    {
        CatalogAdministrationOutcome.Succeeded when created => Created(string.Empty, result.Value),
        CatalogAdministrationOutcome.Succeeded => Ok(result.Value),
        CatalogAdministrationOutcome.ValidationFailed => BadRequest(Problem(400, "VALIDATION_FAILED")),
        CatalogAdministrationOutcome.NotFound => NotFound(Problem(404, "RESOURCE_NOT_FOUND")),
        CatalogAdministrationOutcome.Conflict => Conflict(Problem(409, "CONCURRENCY_CONFLICT")),
        CatalogAdministrationOutcome.SlugConflict => Conflict(Problem(409, "SLUG_CONFLICT")),
        CatalogAdministrationOutcome.PublicationPreconditionFailed =>
            UnprocessableEntity(Problem(422, "PUBLICATION_PRECONDITION_FAILED")),
        _ => throw new InvalidOperationException(),
    };

    private ActionResult<AdminPageManifest> MapPage(AdminPageMutationResult result) => result.Outcome switch
    {
        AdminPageMutationOutcome.Succeeded => Ok(result.Manifest),
        AdminPageMutationOutcome.ValidationFailed => BadRequest(Problem(400, "VALIDATION_FAILED")),
        AdminPageMutationOutcome.NotFound => NotFound(Problem(404, "RESOURCE_NOT_FOUND")),
        AdminPageMutationOutcome.Conflict => Conflict(Problem(409, "CONCURRENCY_CONFLICT")),
        AdminPageMutationOutcome.PublicationPreconditionFailed =>
            UnprocessableEntity(Problem(422, "PUBLICATION_PRECONDITION_FAILED")),
        AdminPageMutationOutcome.ProviderFailed => StatusCode(503, Problem(503, "PROVIDER_UNAVAILABLE")),
        _ => throw new InvalidOperationException(),
    };

    private Guid Actor() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static bool IsTaxonomyKind(string kind) =>
        kind is "genres" or "authors" or "translation-groups";
    private ProblemDetails Problem(int status, string code) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>().Create(HttpContext, status, code, code,
            "The catalog command could not be completed.");
}

public sealed record SaveComicRequest(
    [Required, MaxLength(200)] string Slug,
    [Required] string Title,
    [Required] string Description,
    [Required] string SeriesStatus,
    [RegularExpression(@".*\S.*", ErrorMessage = "CoverAssetId cannot be blank.")] string? CoverAssetId,
    [Range(0, long.MaxValue)] long DefaultPriceCoins,
    bool DefaultVipEligible,
    long? ExpectedVersion,
    IReadOnlyList<Guid>? GenreIds,
    IReadOnlyList<Guid>? AuthorIds,
    IReadOnlyList<Guid>? TranslationGroupIds)
{
    public SaveComicInput ToInput(Guid? id) => new(id, Slug, Title, Description, SeriesStatus,
        CoverAssetId, DefaultPriceCoins, DefaultVipEligible, ExpectedVersion,
        GenreIds ?? [], AuthorIds ?? [], TranslationGroupIds ?? []);
}

public sealed record SaveChapterRequest(
    Guid ComicId,
    [Required, MaxLength(120)] string ChapterLabel,
    int SortOrder,
    [Range(0, long.MaxValue)] long? PriceOverrideCoins,
    bool? VipOverride,
    long? ExpectedVersion)
{
    public SaveChapterInput ToInput(Guid? id) => new(id, ComicId, ChapterLabel, SortOrder,
        PriceOverrideCoins, VipOverride, ExpectedVersion);
}

public sealed record CreateTaxonomyRequest(
    [Required] string Name,
    [MaxLength(200), RegularExpression(@".*\S.*", ErrorMessage = "Slug cannot be blank.")] string? Slug);
public sealed record SaveFeaturedRequest(IReadOnlyList<Guid> ComicIds, [Range(1, long.MaxValue)] long ExpectedVersion);
public sealed class AdminAppendPagesRequest
{
    [FromForm(Name = "expectedVersion"), Range(1, long.MaxValue)] public long ExpectedVersion { get; init; }
    [FromForm(Name = "files"), Required] public List<IFormFile> Files { get; init; } = [];
}
public sealed class AdminReplacePageRequest
{
    [FromForm(Name = "expectedVersion"), Range(1, long.MaxValue)] public long ExpectedVersion { get; init; }
    [FromForm(Name = "file"), Required] public IFormFile? File { get; init; }
}
public sealed record ReorderPagesRequest(
    [property: Range(1, long.MaxValue)] long ExpectedVersion,
    [property: Required, MinLength(1)] IReadOnlyList<Guid> PageIds);

[ApiController]
[Route("api/v1/featured")]
public sealed class FeaturedController(IFeaturedCatalogStore store) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<FeaturedPublicItem>> Get(CancellationToken token) => store.GetPublicAsync(token);
}
