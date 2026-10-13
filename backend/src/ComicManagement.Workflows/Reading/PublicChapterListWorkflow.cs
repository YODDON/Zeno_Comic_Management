using ComicManagement.Catalog.Browsing;
using ComicManagement.Reading.Access;
using ComicManagement.Shared.Identity;

namespace ComicManagement.Workflows.Reading;

public sealed class PublicChapterListWorkflow(
    PublicCatalogHandler catalogHandler,
    ChapterAccessHandler accessHandler,
    ICurrentUser currentUser)
{
    private const int MaximumSnapshotAttempts = 2;

    public async Task<PublicChapterListResult> ListAsync(
        Guid comicId,
        int? page,
        CancellationToken cancellationToken = default)
    {
        PublicChapterListResult result = null!;
        IReadOnlyDictionary<Guid, ChapterAccessDecision> accessByChapter =
            new Dictionary<Guid, ChapterAccessDecision>();
        for (var attempt = 0; attempt < MaximumSnapshotAttempts; attempt++)
        {
            result = await catalogHandler.ListChaptersAsync(comicId, page, cancellationToken);
            if (result.Outcome != PublicCatalogOutcome.Succeeded || result.Page is null)
            {
                return result;
            }

            accessByChapter = await accessHandler.GetAccessBatchAsync(
                result.Page.Items.Select(chapter => chapter.Id).ToArray(),
                currentUser.UserId,
                cancellationToken);
            if (result.Page.Items.All(chapter => accessByChapter.ContainsKey(chapter.Id)))
            {
                return EnrichPage(result, accessByChapter);
            }
        }

        // Publication may change between the Catalog and Reading projections.
        // Never retain metadata for a chapter that failed the authoritative
        // Reading visibility check, even if the retry also observed a change.
        var safeItems = result.Page!.Items
            .Where(chapter => accessByChapter.ContainsKey(chapter.Id))
            .Select(chapter => Enrich(chapter, accessByChapter[chapter.Id]))
            .ToArray();
        var removedCount = result.Page.Items.Count - safeItems.Length;
        var safeTotalItems = Math.Max(0, result.Page.TotalItems - removedCount);
        var safeTotalPages = safeTotalItems / result.Page.PageSize +
            (safeTotalItems % result.Page.PageSize == 0 ? 0 : 1);

        return result with
        {
            Page = result.Page with
            {
                Items = safeItems,
                TotalItems = safeTotalItems,
                TotalPages = safeTotalPages,
            },
        };
    }

    private static PublicChapterListResult EnrichPage(
        PublicChapterListResult result,
        IReadOnlyDictionary<Guid, ChapterAccessDecision> accessByChapter)
    {
        var items = result.Page!.Items
            .Select(chapter => Enrich(chapter, accessByChapter[chapter.Id]))
            .ToArray();
        return result with { Page = result.Page with { Items = items } };
    }

    private static PublicChapterListItem Enrich(
        PublicChapterListItem chapter,
        ChapterAccessDecision access)
    {
        return chapter with
        {
            AccessSummary = new PublicChapterAccessSummary(
                access.AccessType,
                access.CanRead,
                access.PriceCoins),
        };
    }
}
