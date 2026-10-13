using ComicManagement.Api.Authentication;
using ComicManagement.Catalog.Browsing;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/comics")]
public sealed class PublicCatalogController(
    PublicCatalogHandler catalogHandler) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PublicComicListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PublicComicListResponse>> ListComics(
        [FromQuery, StringLength(PublicCatalogHandler.MaximumSearchLength)] string? search,
        [FromQuery, StringLength(Genre.MaximumSlugLength)] string? genre,
        [FromQuery, AllowedValues(
            ComicSeriesStatuses.Ongoing,
            ComicSeriesStatuses.Completed,
            ComicSeriesStatuses.Hiatus)] string? seriesStatus,
        [FromQuery, StringLength(32, MinimumLength = 32)] string? cursor,
        CancellationToken cancellationToken,
        [FromQuery, Range(1, PublicCatalogHandler.MaximumPageSize)] int limit =
            PublicCatalogHandler.DefaultPageSize)
    {
        var result = await catalogHandler.ListComicsAsync(
            search,
            genre,
            seriesStatus,
            cursor,
            limit,
            cancellationToken);
        if (result.Outcome == PublicCatalogOutcome.ValidationFailed)
        {
            return BadRequest(ValidationProblem(
                "Search, filters, cursor or limit are invalid. Limit must be between 1 and 100."));
        }

        var page = result.Page ?? throw new InvalidOperationException(
            "A successful comic list query requires a page.");
        return Ok(new PublicComicListResponse(
            page.Items.Select(ToResponse).ToArray(),
            page.NextCursor));
    }

    [HttpGet("{slug}")]
    [ProducesResponseType<PublicComicDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicComicDetailResponse>> GetComic(
        [StringLength(Comic.MaximumSlugLength, MinimumLength = 1)] string slug,
        CancellationToken cancellationToken)
    {
        var result = await catalogHandler.GetComicAsync(slug, cancellationToken);
        return result.Outcome switch
        {
            PublicCatalogOutcome.Succeeded => Ok(ToResponse(
                result.Detail ?? throw new InvalidOperationException(
                    "A successful comic detail query requires a detail."))),
            PublicCatalogOutcome.ValidationFailed => BadRequest(ValidationProblem(
                "A valid comic slug is required.")),
            PublicCatalogOutcome.NotFound => NotFound(NotFoundProblem()),
            _ => throw new InvalidOperationException("Unknown public catalog outcome."),
        };
    }

    [HttpGet("{comicId:guid}/chapters")]
    [ProducesResponseType<PublicChapterListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicChapterListResponse>> ListChapters(
        Guid comicId,
        CancellationToken cancellationToken,
        [FromQuery, Range(1, PublicCatalogHandler.MaximumPublicChapterPage)] int page = 1)
    {
        var result = await catalogHandler.ListChaptersAsync(
            comicId,
            page,
            cancellationToken);
        if (result.Outcome == PublicCatalogOutcome.ValidationFailed)
        {
            return BadRequest(ValidationProblem(
                "Comic ID or page is invalid."));
        }

        if (result.Outcome == PublicCatalogOutcome.NotFound)
        {
            return NotFound(NotFoundProblem());
        }

        var responsePage = result.Page ?? throw new InvalidOperationException(
            "A successful chapter list query requires a page.");
        return Ok(new PublicChapterListResponse(
            responsePage.Items.Select(ToResponse).ToArray(),
            responsePage.Page,
            responsePage.PageSize,
            responsePage.TotalItems,
            responsePage.TotalPages));
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

    private static PublicComicListItemResponse ToResponse(PublicComicListItem item) => new(
        item.Id,
        item.Slug,
        item.Title,
        item.CoverAssetId,
        item.SeriesStatus,
        item.UpdateSortAt,
        item.Genres.Select(ToResponse).ToArray());

    private static PublicComicDetailResponse ToResponse(PublicComicDetail detail) => new(
        detail.Id,
        detail.Slug,
        detail.Title,
        detail.Description,
        detail.CoverAssetId,
        detail.SeriesStatus,
        detail.FirstPublishedAt,
        detail.UpdatedAt,
        detail.Genres.Select(ToResponse).ToArray(),
        detail.Authors.Select(ToResponse).ToArray(),
        detail.TranslationGroups.Select(ToResponse).ToArray());

    private static PublicChapterListItemResponse ToResponse(PublicChapterListItem chapter) => new(
        chapter.Id,
        chapter.ChapterLabel,
        chapter.SortOrder,
        chapter.PublicationStatus,
        new PublicChapterAccessSummaryResponse(
            chapter.AccessSummary.AccessType,
            chapter.AccessSummary.CanRead,
            chapter.AccessSummary.FinalPriceCoins));

    private static CatalogReferenceResponse ToResponse(CatalogReference reference) => new(
        reference.Id,
        reference.Name,
        reference.Slug);
}

public sealed record PublicComicListResponse(
    IReadOnlyList<PublicComicListItemResponse> Items,
    string? NextCursor);

public sealed record PublicComicListItemResponse(
    Guid Id,
    string Slug,
    string Title,
    string? CoverAssetId,
    string SeriesStatus,
    DateTimeOffset UpdateSortAt,
    IReadOnlyList<CatalogReferenceResponse> Genres);

public sealed record PublicComicDetailResponse(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    string? CoverAssetId,
    string SeriesStatus,
    DateTimeOffset FirstPublishedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CatalogReferenceResponse> Genres,
    IReadOnlyList<CatalogReferenceResponse> Authors,
    IReadOnlyList<CatalogReferenceResponse> TranslationGroups);

public sealed record CatalogReferenceResponse(Guid Id, string Name, string? Slug);

public sealed record PublicChapterListResponse(
    IReadOnlyList<PublicChapterListItemResponse> Items,
    [property: Range(1, PublicCatalogHandler.MaximumPublicChapterPage)]
    int Page,
    [property: Range(
        PublicCatalogHandler.PublicChapterPageSize,
        PublicCatalogHandler.PublicChapterPageSize)]
    int PageSize,
    [property: Range(0, int.MaxValue)]
    int TotalItems,
    [property: Range(0, int.MaxValue)]
    int TotalPages);

public sealed record PublicChapterListItemResponse(
    Guid Id,
    string ChapterLabel,
    int SortOrder,
    string PublicationStatus,
    PublicChapterAccessSummaryResponse AccessSummary);

public sealed record PublicChapterAccessSummaryResponse(
    string AccessType,
    bool CanRead,
    long FinalPriceCoins);
