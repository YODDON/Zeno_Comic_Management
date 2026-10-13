using System.Buffers.Binary;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;

namespace ComicManagement.Catalog.Browsing;

public enum PublicCatalogOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
}

public sealed record CatalogReference(Guid Id, string Name, string? Slug = null);

public sealed record PublicComicListItem(
    Guid Id,
    string Slug,
    string Title,
    string? CoverAssetId,
    string SeriesStatus,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CatalogReference> Genres);

public sealed record PublicComicListPage(
    IReadOnlyList<PublicComicListItem> Items,
    string? NextCursor);

public sealed record PublicComicDetail(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    string? CoverAssetId,
    string SeriesStatus,
    DateTimeOffset FirstPublishedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CatalogReference> Genres,
    IReadOnlyList<CatalogReference> Authors,
    IReadOnlyList<CatalogReference> TranslationGroups);

public sealed record PublicChapterAccessSummary(
    string AccessType,
    bool CanRead,
    long FinalPriceCoins);

public sealed record PublicChapterListItem(
    Guid Id,
    string ChapterLabel,
    int SortOrder,
    string PublicationStatus,
    PublicChapterAccessSummary AccessSummary);

public sealed record PublicChapterListPage(
    IReadOnlyList<PublicChapterListItem> Items,
    string? NextCursor);

public sealed record PublicComicListResult(
    PublicCatalogOutcome Outcome,
    PublicComicListPage? Page = null);

public sealed record PublicComicDetailResult(
    PublicCatalogOutcome Outcome,
    PublicComicDetail? Detail = null);

public sealed record PublicChapterListResult(
    PublicCatalogOutcome Outcome,
    PublicChapterListPage? Page = null);

public sealed record PublicComicListCriteria(
    string? Search,
    string? GenreSlug,
    string? SeriesStatus,
    DateTimeOffset? BeforeUpdatedAt,
    Guid? BeforeId);

public sealed record PublicChapterListCriteria(
    Guid ComicId,
    int? AfterSortOrder,
    Guid? AfterId);

public sealed record PublicChapterStorePage(
    bool ComicFound,
    IReadOnlyList<PublicChapterListItem> Items);

public interface IPublicCatalogStore
{
    Task<IReadOnlyList<PublicComicListItem>> ListComicsAsync(
        PublicComicListCriteria criteria,
        int take,
        CancellationToken cancellationToken = default);

    Task<PublicComicDetail?> GetComicBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default);

    Task<PublicChapterStorePage> ListChaptersAsync(
        PublicChapterListCriteria criteria,
        int take,
        CancellationToken cancellationToken = default);
}

public sealed class PublicCatalogHandler(IPublicCatalogStore store)
{
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 200;

    public async Task<PublicComicListResult> ListComicsAsync(
        string? search,
        string? genreSlug,
        string? seriesStatus,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedSearch = NormalizeOptional(search);
        var normalizedGenre = NormalizeOptional(genreSlug);
        var pageSize = limit ?? DefaultPageSize;
        if (normalizedSearch?.Length > MaximumSearchLength ||
            normalizedGenre?.Length > Genre.MaximumSlugLength ||
            (seriesStatus is not null && !ComicSeriesStatuses.IsDefined(seriesStatus)) ||
            pageSize is < 1 or > MaximumPageSize ||
            !PublicCatalogCursor.TryDecodeComic(cursor, out var comicCursor))
        {
            return new PublicComicListResult(PublicCatalogOutcome.ValidationFailed);
        }

        var items = await store.ListComicsAsync(
            new PublicComicListCriteria(
                normalizedSearch,
                normalizedGenre,
                seriesStatus,
                comicCursor?.UpdatedAt,
                comicCursor?.Id),
            pageSize + 1,
            cancellationToken);

        var pageItems = items.Take(pageSize).ToArray();
        var nextCursor = items.Count > pageSize
            ? PublicCatalogCursor.EncodeComic(pageItems[^1].UpdatedAt, pageItems[^1].Id)
            : null;
        return new PublicComicListResult(
            PublicCatalogOutcome.Succeeded,
            new PublicComicListPage(pageItems, nextCursor));
    }

    public async Task<PublicComicDetailResult> GetComicAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        if (!Comic.IsValidSlug(slug))
        {
            return new PublicComicDetailResult(PublicCatalogOutcome.ValidationFailed);
        }

        var detail = await store.GetComicBySlugAsync(slug, cancellationToken);
        return detail is null
            ? new PublicComicDetailResult(PublicCatalogOutcome.NotFound)
            : new PublicComicDetailResult(PublicCatalogOutcome.Succeeded, detail);
    }

    public async Task<PublicChapterListResult> ListChaptersAsync(
        Guid comicId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (comicId == Guid.Empty ||
            pageSize is < 1 or > MaximumPageSize ||
            !PublicCatalogCursor.TryDecodeChapter(cursor, out var chapterCursor))
        {
            return new PublicChapterListResult(PublicCatalogOutcome.ValidationFailed);
        }

        var storePage = await store.ListChaptersAsync(
            new PublicChapterListCriteria(
                comicId,
                chapterCursor?.SortOrder,
                chapterCursor?.Id),
            pageSize + 1,
            cancellationToken);
        if (!storePage.ComicFound)
        {
            return new PublicChapterListResult(PublicCatalogOutcome.NotFound);
        }

        var pageItems = storePage.Items.Take(pageSize).ToArray();
        var nextCursor = storePage.Items.Count > pageSize
            ? PublicCatalogCursor.EncodeChapter(pageItems[^1].SortOrder, pageItems[^1].Id)
            : null;
        return new PublicChapterListResult(
            PublicCatalogOutcome.Succeeded,
            new PublicChapterListPage(pageItems, nextCursor));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class PublicCatalogCursor
{
    private const int ComicCursorLength = sizeof(long) + 16;
    private const int ChapterCursorLength = sizeof(int) + 16;

    public static string EncodeComic(DateTimeOffset updatedAt, Guid id)
    {
        Span<byte> bytes = stackalloc byte[ComicCursorLength];
        BinaryPrimitives.WriteInt64BigEndian(bytes, updatedAt.UtcTicks);
        id.TryWriteBytes(bytes[sizeof(long)..]);
        return Encode(bytes);
    }

    public static string EncodeChapter(int sortOrder, Guid id)
    {
        Span<byte> bytes = stackalloc byte[ChapterCursorLength];
        BinaryPrimitives.WriteInt32BigEndian(bytes, sortOrder);
        id.TryWriteBytes(bytes[sizeof(int)..]);
        return Encode(bytes);
    }

    public static bool TryDecodeComic(string? cursor, out ComicCursor? value)
    {
        value = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        if (!TryDecode(cursor, ComicCursorLength, out var bytes))
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        value = new ComicCursor(
            new DateTimeOffset(ticks, TimeSpan.Zero),
            new Guid(bytes.AsSpan(sizeof(long), 16)));
        return value.Id != Guid.Empty;
    }

    public static bool TryDecodeChapter(string? cursor, out ChapterCursor? value)
    {
        value = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        if (!TryDecode(cursor, ChapterCursorLength, out var bytes))
        {
            return false;
        }

        value = new ChapterCursor(
            BinaryPrimitives.ReadInt32BigEndian(bytes),
            new Guid(bytes.AsSpan(sizeof(int), 16)));
        return value.Id != Guid.Empty;
    }

    private static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryDecode(string cursor, int expectedLength, out byte[] bytes)
    {
        bytes = [];
        var expectedEncodedLength = ((expectedLength + 2) / 3 * 4) -
            ((3 - expectedLength % 3) % 3);
        if (cursor.Length != expectedEncodedLength)
        {
            return false;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - normalized.Length % 4) % 4),
                '=');
            bytes = Convert.FromBase64String(normalized);
            return bytes.Length == expectedLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal sealed record ComicCursor(DateTimeOffset UpdatedAt, Guid Id);

    internal sealed record ChapterCursor(int SortOrder, Guid Id);
}
