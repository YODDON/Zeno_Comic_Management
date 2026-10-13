using ComicManagement.Catalog.Browsing;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class PublicCatalogStore(AppDbContext dbContext) : IPublicCatalogStore
{
    public async Task<IReadOnlyList<PublicComicListItem>> ListComicsAsync(
        PublicComicListCriteria criteria,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Comics
            .AsNoTracking()
            .Where(comic =>
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

        if (criteria.Search is not null)
        {
            var searchPattern = $"%{EscapeLikePattern(criteria.Search)}%";
            query = query.Where(comic =>
                EF.Functions.ILike(comic.Title, searchPattern, "\\"));
        }

        if (criteria.GenreSlug is not null)
        {
            query = query.Where(comic => dbContext.ComicGenres.Any(link =>
                link.ComicId == comic.Id &&
                dbContext.Genres.Any(genre =>
                    genre.Id == link.GenreId && genre.Slug == criteria.GenreSlug)));
        }

        if (criteria.SeriesStatus is not null)
        {
            query = query.Where(comic => comic.SeriesStatus == criteria.SeriesStatus);
        }

        if (criteria.BeforeUpdatedAt is not null && criteria.BeforeId is not null)
        {
            var beforeUpdatedAt = criteria.BeforeUpdatedAt.Value;
            var beforeId = criteria.BeforeId.Value;
            query = query.Where(comic =>
                comic.UpdatedAt < beforeUpdatedAt ||
                (comic.UpdatedAt == beforeUpdatedAt && comic.Id.CompareTo(beforeId) < 0));
        }

        var rows = await query
            .OrderByDescending(comic => comic.UpdatedAt)
            .ThenByDescending(comic => comic.Id)
            .Take(take)
            .Select(comic => new
            {
                comic.Id,
                comic.Slug,
                comic.Title,
                comic.CoverAssetId,
                comic.SeriesStatus,
                comic.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var comicIds = rows.Select(row => row.Id).ToArray();
        var genreRows = await dbContext.ComicGenres
            .AsNoTracking()
            .Where(link => comicIds.Contains(link.ComicId))
            .Join(
                dbContext.Genres.AsNoTracking(),
                link => link.GenreId,
                genre => genre.Id,
                (link, genre) => new { link.ComicId, genre.Id, genre.Name, genre.Slug })
            .OrderBy(row => row.Name)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);
        var genresByComic = genreRows.ToLookup(row => row.ComicId);

        return rows.Select(row => new PublicComicListItem(
            row.Id,
            row.Slug,
            row.Title,
            row.CoverAssetId,
            row.SeriesStatus,
            row.UpdatedAt,
            genresByComic[row.Id]
                .Select(genre => new CatalogReference(genre.Id, genre.Name, genre.Slug))
                .ToArray()))
            .ToArray();
    }

    public async Task<PublicComicDetail?> GetComicBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.Comics
            .AsNoTracking()
            .Where(comic =>
                comic.Slug == slug &&
                comic.PublicationStatus == ComicPublicationStatuses.Published &&
                comic.FirstPublishedAt != null &&
                dbContext.Chapters.Any(chapter =>
                    chapter.ComicId == comic.Id &&
                    chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                    dbContext.ChapterPages.Any(page =>
                        page.ChapterId == chapter.Id &&
                        page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                        page.Width > 0 &&
                        page.Height > 0)))
            .Select(comic => new
            {
                comic.Id,
                comic.Slug,
                comic.Title,
                comic.Description,
                comic.CoverAssetId,
                comic.SeriesStatus,
                FirstPublishedAt = comic.FirstPublishedAt!.Value,
                comic.UpdatedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var genres = await dbContext.ComicGenres
            .AsNoTracking()
            .Where(link => link.ComicId == row.Id)
            .Join(
                dbContext.Genres.AsNoTracking(),
                link => link.GenreId,
                genre => genre.Id,
                (_, genre) => genre)
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .Select(genre => new CatalogReference(genre.Id, genre.Name, genre.Slug))
            .ToListAsync(cancellationToken);
        var authors = await dbContext.ComicAuthors
            .AsNoTracking()
            .Where(link => link.ComicId == row.Id)
            .Join(
                dbContext.Authors.AsNoTracking(),
                link => link.AuthorId,
                author => author.Id,
                (_, author) => author)
            .OrderBy(author => author.Name)
            .ThenBy(author => author.Id)
            .Select(author => new CatalogReference(author.Id, author.Name))
            .ToListAsync(cancellationToken);
        var translationGroups = await dbContext.ComicTranslationGroups
            .AsNoTracking()
            .Where(link => link.ComicId == row.Id)
            .Join(
                dbContext.TranslationGroups.AsNoTracking(),
                link => link.TranslationGroupId,
                group => group.Id,
                (_, group) => group)
            .OrderBy(group => group.Name)
            .ThenBy(group => group.Id)
            .Select(group => new CatalogReference(group.Id, group.Name))
            .ToListAsync(cancellationToken);

        return new PublicComicDetail(
            row.Id,
            row.Slug,
            row.Title,
            row.Description,
            row.CoverAssetId,
            row.SeriesStatus,
            row.FirstPublishedAt,
            row.UpdatedAt,
            genres,
            authors,
            translationGroups);
    }

    public async Task<PublicChapterStorePage> ListChaptersAsync(
        PublicChapterListCriteria criteria,
        int take,
        CancellationToken cancellationToken = default)
    {
        var comic = await dbContext.Comics
            .AsNoTracking()
            .Where(value =>
                value.Id == criteria.ComicId &&
                value.PublicationStatus == ComicPublicationStatuses.Published &&
                value.FirstPublishedAt != null &&
                dbContext.Chapters.Any(chapter =>
                    chapter.ComicId == value.Id &&
                    chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                    dbContext.ChapterPages.Any(page =>
                        page.ChapterId == chapter.Id &&
                        page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                        page.Width > 0 &&
                        page.Height > 0)))
            .Select(value => new { value.Id, value.DefaultPriceCoins })
            .SingleOrDefaultAsync(cancellationToken);
        if (comic is null)
        {
            return new PublicChapterStorePage(false, []);
        }

        var query = dbContext.Chapters
            .AsNoTracking()
            .Where(chapter =>
                chapter.ComicId == comic.Id &&
                chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                dbContext.ChapterPages.Any(page =>
                    page.ChapterId == chapter.Id &&
                    page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                    page.Width > 0 &&
                    page.Height > 0));

        if (criteria.AfterSortOrder is not null && criteria.AfterId is not null)
        {
            var afterSortOrder = criteria.AfterSortOrder.Value;
            var afterId = criteria.AfterId.Value;
            query = query.Where(chapter =>
                chapter.SortOrder > afterSortOrder ||
                (chapter.SortOrder == afterSortOrder && chapter.Id.CompareTo(afterId) > 0));
        }

        var rows = await query
            .OrderBy(chapter => chapter.SortOrder)
            .ThenBy(chapter => chapter.Id)
            .Take(take)
            .Select(chapter => new
            {
                chapter.Id,
                chapter.ChapterLabel,
                chapter.SortOrder,
                chapter.PublicationStatus,
                EffectivePriceCoins = chapter.PriceOverrideCoins ?? comic.DefaultPriceCoins,
            })
            .ToListAsync(cancellationToken);

        return new PublicChapterStorePage(
            true,
            rows.Select(row => new PublicChapterListItem(
                row.Id,
                row.ChapterLabel,
                row.SortOrder,
                row.PublicationStatus,
                new PublicChapterAccessSummary(
                    row.EffectivePriceCoins == 0 ? "Free" : "Locked",
                    row.EffectivePriceCoins == 0,
                    row.EffectivePriceCoins)))
                .ToArray());
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
