using ComicManagement.Catalog.Browsing;
using ComicManagement.Reading.Access;
using ComicManagement.Shared.Identity;

namespace ComicManagement.Workflows.Reading;

public sealed class PublicChapterListWorkflow(
    PublicCatalogHandler catalogHandler,
    ChapterAccessHandler accessHandler,
    ICurrentUser currentUser)
{
    public async Task<PublicChapterListResult> ListAsync(
        Guid comicId,
        int? page,
        CancellationToken cancellationToken = default)
    {
        var result = await catalogHandler.ListChaptersAsync(comicId, page, cancellationToken);
        if (result.Outcome != PublicCatalogOutcome.Succeeded || result.Page is null)
        {
            return result;
        }

        var accessByChapter = await accessHandler.GetAccessBatchAsync(
            result.Page.Items.Select(chapter => chapter.Id).ToArray(),
            currentUser.UserId,
            cancellationToken);
        var items = result.Page.Items
            .Select(chapter => Enrich(chapter, accessByChapter))
            .ToArray();

        return result with { Page = result.Page with { Items = items } };
    }

    private static PublicChapterListItem Enrich(
        PublicChapterListItem chapter,
        IReadOnlyDictionary<Guid, ChapterAccessDecision> accessByChapter)
    {
        if (!accessByChapter.TryGetValue(chapter.Id, out var access))
        {
            return chapter with
            {
                AccessSummary = new PublicChapterAccessSummary(
                    ChapterAccessTypes.Unavailable,
                    false,
                    chapter.AccessSummary.FinalPriceCoins),
            };
        }

        return chapter with
        {
            AccessSummary = new PublicChapterAccessSummary(
                access.AccessType,
                access.CanRead,
                access.PriceCoins),
        };
    }
}
