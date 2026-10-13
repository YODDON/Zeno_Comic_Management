using ComicManagement.Catalog.Browsing;
using Xunit;

namespace ComicManagement.Catalog.UnitTests;

public sealed class PublicCatalogHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Comic_list_uses_thirty_item_page_and_round_trips_keyset_cursor()
    {
        var items = Enumerable.Range(1, 31)
            .Select(index => CreateComic(index, Now.AddMinutes(-index)))
            .ToArray();
        var store = new RecordingStore { ComicItems = items };
        var handler = new PublicCatalogHandler(store);

        var first = await handler.ListComicsAsync(null, null, null, null, null);

        Assert.Equal(PublicCatalogOutcome.Succeeded, first.Outcome);
        Assert.Equal(30, first.Page!.Items.Count);
        Assert.NotNull(first.Page.NextCursor);
        Assert.Equal(31, store.Take);

        store.ComicItems = [];
        var second = await handler.ListComicsAsync(
            null,
            null,
            null,
            first.Page.NextCursor,
            null);

        Assert.Equal(items[29].UpdateSortAt, store.ComicCriteria!.BeforeUpdateSortAt);
        Assert.Equal(items[29].Id, store.ComicCriteria.BeforeId);
        Assert.Null(second.Page!.NextCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PublicCatalogHandler.MaximumPublicChapterPage + 1)]
    public async Task Chapter_list_rejects_invalid_page_without_querying_store(int page)
    {
        var store = new RecordingStore();
        var handler = new PublicCatalogHandler(store);

        var result = await handler.ListChaptersAsync(Guid.NewGuid(), page);

        Assert.Equal(PublicCatalogOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task Chapter_list_uses_fixed_page_size_and_returns_total_metadata()
    {
        var store = new RecordingStore
        {
            ChapterPage = new PublicChapterStorePage(true, [], 61),
        };
        var handler = new PublicCatalogHandler(store);

        var result = await handler.ListChaptersAsync(Guid.NewGuid(), 3);

        Assert.Equal(PublicCatalogOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, store.ChapterCriteria!.Page);
        Assert.Equal(PublicCatalogHandler.PublicChapterPageSize, store.Take);
        Assert.Equal(3, result.Page!.Page);
        Assert.Equal(30, result.Page.PageSize);
        Assert.Equal(61, result.Page.TotalItems);
        Assert.Equal(3, result.Page.TotalPages);
    }

    [Fact]
    public async Task Comic_list_normalizes_filters_and_rejects_unknown_series_status()
    {
        var store = new RecordingStore();
        var handler = new PublicCatalogHandler(store);

        var success = await handler.ListComicsAsync(
            "  Hero  ",
            " action ",
            "Ongoing",
            null,
            10);
        var invalid = await handler.ListComicsAsync(
            null,
            null,
            "Cancelled",
            null,
            null);

        Assert.Equal(PublicCatalogOutcome.Succeeded, success.Outcome);
        Assert.Equal("Hero", store.ComicCriteria!.Search);
        Assert.Equal("action", store.ComicCriteria.GenreSlug);
        Assert.Equal("Ongoing", store.ComicCriteria.SeriesStatus);
        Assert.Equal(PublicCatalogOutcome.ValidationFailed, invalid.Outcome);
        Assert.Equal(1, store.CallCount);
    }

    [Fact]
    public async Task Hidden_or_missing_comic_is_reported_as_not_found()
    {
        var handler = new PublicCatalogHandler(new RecordingStore());

        var result = await handler.GetComicAsync("missing-comic");

        Assert.Equal(PublicCatalogOutcome.NotFound, result.Outcome);
    }

    private static PublicComicListItem CreateComic(int index, DateTimeOffset updateSortAt) => new(
        Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
        $"comic-{index}",
        $"Comic {index}",
        null,
        "Ongoing",
        updateSortAt,
        []);

    private sealed class RecordingStore : IPublicCatalogStore
    {
        public IReadOnlyList<PublicComicListItem> ComicItems { get; set; } = [];

        public PublicComicListCriteria? ComicCriteria { get; private set; }

        public PublicChapterListCriteria? ChapterCriteria { get; private set; }

        public PublicChapterStorePage ChapterPage { get; set; } = new(true, [], 0);

        public int Take { get; private set; }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<PublicComicListItem>> ListComicsAsync(
            PublicComicListCriteria criteria,
            int take,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ComicCriteria = criteria;
            Take = take;
            return Task.FromResult(ComicItems);
        }

        public Task<PublicComicDetail?> GetComicBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<PublicComicDetail?>(null);
        }

        public Task<PublicChapterStorePage> ListChaptersAsync(
            PublicChapterListCriteria criteria,
            int take,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ChapterCriteria = criteria;
            Take = take;
            return Task.FromResult(ChapterPage);
        }
    }
}
