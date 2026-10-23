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
        var query = PublicComicVisibilityQuery.Apply(dbContext).AsNoTracking();

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

        var projectedQuery = query.Select(comic => new
        {
            comic.Id,
            comic.Slug,
            comic.Title,
            comic.CoverAssetId,
            comic.SeriesStatus,
            UpdateSortAt = dbContext.Chapters
                .Where(chapter =>
                    chapter.ComicId == comic.Id &&
                    chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                    dbContext.ChapterPages.Any(page =>
                        page.ChapterId == chapter.Id &&
                        page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                        page.Width > 0 &&
                        page.Height > 0))
                .Max(chapter => chapter.PublishedAt) ?? comic.CreatedAt,
        });

        if (criteria.BeforeUpdateSortAt is not null && criteria.BeforeId is not null)
        {
            var beforeUpdateSortAt = criteria.BeforeUpdateSortAt.Value;
            var beforeId = criteria.BeforeId.Value;
            projectedQuery = projectedQuery.Where(comic =>
                comic.UpdateSortAt < beforeUpdateSortAt ||
                (comic.UpdateSortAt == beforeUpdateSortAt && comic.Id.CompareTo(beforeId) < 0));
        }

        var rows = await projectedQuery
            .OrderByDescending(comic => comic.UpdateSortAt)
            .ThenByDescending(comic => comic.Id)
            .Take(take)
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
            row.UpdateSortAt,
            genresByComic[row.Id]
                .Select(genre => new CatalogReference(genre.Id, genre.Name, genre.Slug))
                .ToArray()))
            .ToArray();
    }

    public async Task<PublicComicDetail?> GetComicBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var row = await PublicComicVisibilityQuery.Apply(dbContext)
            .AsNoTracking()
            .Where(comic => comic.Slug == slug)
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
        var comic = await PublicComicVisibilityQuery.Apply(dbContext)
            .AsNoTracking()
            .Where(value => value.Id == criteria.ComicId)
            .Select(value => new
            {
                value.Id,
                value.DefaultPriceCoins,
                TotalItems = dbContext.Chapters.Count(chapter =>
                    chapter.ComicId == value.Id &&
                    chapter.PublicationStatus == ChapterPublicationStatuses.Published &&
                    dbContext.ChapterPages.Any(page =>
                        page.ChapterId == chapter.Id &&
                        page.AssetStatus == ChapterPageAssetStatuses.Ready &&
                        page.Width > 0 &&
                        page.Height > 0)),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (comic is null)
        {
            return new PublicChapterStorePage(false, [], 0);
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

        var skip = checked((criteria.Page - 1) * take);
        var rows = await query
            .OrderBy(chapter => chapter.SortOrder)
            .ThenBy(chapter => chapter.Id)
            .Skip(skip)
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
                .ToArray(),
            comic.TotalItems);
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
