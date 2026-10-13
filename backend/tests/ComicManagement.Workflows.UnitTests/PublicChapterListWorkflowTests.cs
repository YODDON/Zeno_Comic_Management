using ComicManagement.Catalog.Browsing;
using ComicManagement.Reading.Access;
using ComicManagement.Shared.Identity;
using ComicManagement.Shared.Time;
using ComicManagement.Workflows.Reading;
using Xunit;

namespace ComicManagement.Workflows.UnitTests;

public sealed class PublicChapterListWorkflowTests
{
    [Fact]
    public async Task Visibility_change_between_reads_retries_and_returns_fresh_access()
    {
        var chapterId = Guid.NewGuid();
        var catalogStore = new FakeCatalogStore(chapterId);
        var accessStore = new SequencedAccessStore(
            chapterId,
            [false, true]);
        var workflow = CreateWorkflow(catalogStore, accessStore);

        var result = await workflow.ListAsync(Guid.NewGuid(), 1);

        var item = Assert.Single(result.Page!.Items);
        Assert.Equal(ChapterAccessTypes.Free, item.AccessSummary.AccessType);
        Assert.True(item.AccessSummary.CanRead);
        Assert.Equal(2, catalogStore.Calls);
        Assert.Equal(2, accessStore.Calls);
    }

    [Fact]
    public async Task Chapter_missing_from_both_access_snapshots_is_removed_fail_closed()
    {
        var chapterId = Guid.NewGuid();
        var catalogStore = new FakeCatalogStore(chapterId);
        var accessStore = new SequencedAccessStore(
            chapterId,
            [false, false]);
        var workflow = CreateWorkflow(catalogStore, accessStore);

        var result = await workflow.ListAsync(Guid.NewGuid(), 1);

        Assert.Empty(result.Page!.Items);
        Assert.Equal(0, result.Page.TotalItems);
        Assert.Equal(0, result.Page.TotalPages);
        Assert.Equal(2, catalogStore.Calls);
        Assert.Equal(2, accessStore.Calls);
    }

    private static PublicChapterListWorkflow CreateWorkflow(
        FakeCatalogStore catalogStore,
        SequencedAccessStore accessStore) => new(
        new PublicCatalogHandler(catalogStore),
        new ChapterAccessHandler(
            accessStore,
            new UnusedContentProvider(),
            new StubClock()),
        new AnonymousCurrentUser());

    private sealed class FakeCatalogStore(Guid chapterId) : IPublicCatalogStore
    {
        internal int Calls { get; private set; }

        public Task<IReadOnlyList<PublicComicListItem>> ListComicsAsync(
            PublicComicListCriteria criteria,
            int take,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PublicComicDetail?> GetComicBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PublicChapterStorePage> ListChaptersAsync(
            PublicChapterListCriteria criteria,
            int take,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PublicChapterStorePage(
                true,
                [new PublicChapterListItem(
                    chapterId,
                    "Chapter private after race",
                    1,
                    "Published",
                    new PublicChapterAccessSummary(ChapterAccessTypes.Locked, false, 0))],
                1));
        }
    }

    private sealed class SequencedAccessStore(
        Guid chapterId,
        IReadOnlyList<bool> visibilityByCall) : IChapterAccessProjectionStore
    {
        internal int Calls { get; private set; }

        public Task<IReadOnlyDictionary<Guid, ChapterAccessProjection>> GetManyAsync(
            IReadOnlyCollection<Guid> chapterIds,
            Guid? userId,
            CancellationToken cancellationToken = default)
        {
            var visible = visibilityByCall[Math.Min(Calls, visibilityByCall.Count - 1)];
            Calls++;
            IReadOnlyDictionary<Guid, ChapterAccessProjection> result = visible
                ? new Dictionary<Guid, ChapterAccessProjection>
                {
                    [chapterId] = new(chapterId, 0, false, false, false),
                }
                : new Dictionary<Guid, ChapterAccessProjection>();
            return Task.FromResult(result);
        }

        public Task<ChapterManifestSource?> GetManifestSourceAsync(
            Guid chapterId,
            Guid? userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ChapterPageContentSource?> GetPageContentSourceAsync(
            Guid chapterId,
            int pageOrder,
            Guid? userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedContentProvider : IChapterPageContentProvider
    {
        public Task<ChapterPageContent?> GetAsync(
            string assetKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class AnonymousCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => false;
        public Guid? UserId => null;
        public bool IsInRole(string role) => false;
    }
}
