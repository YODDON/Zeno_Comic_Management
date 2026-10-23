using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;

namespace ComicManagement.Persistence;

internal static class PublicComicVisibilityQuery
{
    public static IQueryable<Comic> Apply(AppDbContext dbContext) =>
        dbContext.Comics.Where(comic =>
            comic.PublicationStatus == ComicPublicationStatuses.Published &&
            comic.FirstPublishedAt != null &&
            dbContext.Chapters.Any(chapter =>
                chapter.ComicId == comic.Id &&
                chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                dbContext.ChapterPages.Any(page =>
                    page.ChapterId == chapter.Id &&
                    page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                    page.Width > 0 &&
                    page.Height > 0)));
}
