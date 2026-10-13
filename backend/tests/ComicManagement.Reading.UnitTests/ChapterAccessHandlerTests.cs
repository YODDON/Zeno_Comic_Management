using ComicManagement.Reading.Access;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Reading.UnitTests;

public sealed class ChapterAccessHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Access_precedence_is_free_then_vip_then_unlock_then_locked()
    {
        var free = Projection(0, vipEligible: true, activeVip: true, unlocked: true);
        var vip = Projection(50, vipEligible: true, activeVip: true, unlocked: true);
        var unlocked = Projection(50, vipEligible: false, activeVip: true, unlocked: true);
        var locked = Projection(50, vipEligible: true, activeVip: false, unlocked: false);
        var store = new FakeStore(free, vip, unlocked, locked);
        var handler = Handler(store);
        var userId = Guid.NewGuid();

        Assert.Equal(ChapterAccessTypes.Free,
            (await handler.GetAccessAsync(free.ChapterId, userId)).Access!.AccessType);
        Assert.Equal(ChapterAccessTypes.VipIncluded,
            (await handler.GetAccessAsync(vip.ChapterId, userId)).Access!.AccessType);
        Assert.Equal(ChapterAccessTypes.CoinUnlocked,
            (await handler.GetAccessAsync(unlocked.ChapterId, userId)).Access!.AccessType);
        var lockedAccess = (await handler.GetAccessAsync(locked.ChapterId, userId)).Access!;
        Assert.Equal(ChapterAccessTypes.Locked, lockedAccess.AccessType);
        Assert.False(lockedAccess.CanRead);
        Assert.False(lockedAccess.NeedsLogin);
    }

    [Fact]
    public async Task Anonymous_paid_access_is_locked_and_requires_login()
    {
        var projection = Projection(25, vipEligible: true, activeVip: true, unlocked: true);
        var result = await Handler(new FakeStore(projection))
            .GetAccessAsync(projection.ChapterId, null);

        Assert.Equal(ChapterAccessOutcome.Succeeded, result.Outcome);
        Assert.Equal(ChapterAccessTypes.Locked, result.Access!.AccessType);
        Assert.False(result.Access.CanRead);
        Assert.True(result.Access.NeedsLogin);
        Assert.Equal(25, result.Access.PriceCoins);
    }

    [Fact]
    public async Task Batch_projection_resolves_all_visible_chapters_in_one_store_call()
    {
        var free = Projection(0);
        var locked = Projection(25);
        var missingId = Guid.NewGuid();
        var store = new FakeStore(free, locked);

        var decisions = await Handler(store).GetAccessBatchAsync(
            [free.ChapterId, locked.ChapterId, missingId], null);

        Assert.Equal(2, decisions.Count);
        Assert.Equal(1, store.BatchCalls);
        Assert.Equal(ChapterAccessTypes.Free, decisions[free.ChapterId].AccessType);
        Assert.Equal(ChapterAccessTypes.Locked, decisions[locked.ChapterId].AccessType);
        Assert.DoesNotContain(missingId, decisions.Keys);
    }

    [Fact]
    public async Task Free_manifest_returns_ordered_page_metadata_without_provider_identity()
    {
        var projection = Projection(0);
        var source = new ChapterManifestSource(
            projection,
            [
                new ChapterManifestPageSource(1, 1200, 1800, "chapter/page-1"),
                new ChapterManifestPageSource(2, 1000, 1500, "chapter/page-2"),
            ]);
        var provider = new FakeContentProvider();

        var result = await Handler(new FakeStore(source), provider)
            .GetManifestAsync(projection.ChapterId, null);

        Assert.Equal(ChapterManifestOutcome.Succeeded, result.Outcome);
        Assert.Equal([1, 2], result.Manifest!.Pages.Select(page => page.Order));
        Assert.Equal(Now, result.Manifest.ManifestIssuedAt);
        Assert.Equal(0, provider.Calls);
        Assert.DoesNotContain(typeof(ChapterManifestPage).GetProperties(), property =>
            property.Name.Contains("Asset", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Manifest_denies_locked_and_fails_secure_for_protected_entitlement()
    {
        var locked = Projection(25);
        var vip = Projection(25, vipEligible: true, activeVip: true);
        var page = new ChapterManifestPageSource(1, 1200, 1800, "chapter/page");
        var provider = new FakeContentProvider();

        var anonymousResult = await Handler(
                new FakeStore(new ChapterManifestSource(locked, [page])), provider)
            .GetManifestAsync(locked.ChapterId, null);
        var authenticatedResult = await Handler(
                new FakeStore(new ChapterManifestSource(locked, [page])), provider)
            .GetManifestAsync(locked.ChapterId, Guid.NewGuid());
        var protectedResult = await Handler(
                new FakeStore(new ChapterManifestSource(vip, [page])), provider)
            .GetManifestAsync(vip.ChapterId, Guid.NewGuid());

        Assert.Equal(ChapterManifestOutcome.AuthenticationRequired, anonymousResult.Outcome);
        Assert.Equal(ChapterManifestOutcome.Forbidden, authenticatedResult.Outcome);
        Assert.Equal(ChapterManifestOutcome.ProtectedDeliveryUnavailable, protectedResult.Outcome);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Free_page_is_returned_only_after_access_and_asset_are_rechecked()
    {
        var projection = Projection(0);
        var page = new ChapterManifestPageSource(1, 1200, 1800, "chapter/page");
        var source = new ChapterPageContentSource(projection, page);
        var store = new FakeStore(call => call is 1 or 2 ? source : null);
        var provider = new FakeContentProvider();

        var result = await Handler(store, provider)
            .GetPageContentAsync(projection.ChapterId, 1, null);

        Assert.Equal(ChapterManifestOutcome.Succeeded, result.Outcome);
        Assert.Equal([1, 2, 3], result.Content!.Bytes);
        Assert.Equal("image/webp", result.Content.ContentType);
        Assert.Equal(2, store.ContentCalls);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Page_fails_closed_when_chapter_changes_after_provider_fetch()
    {
        var free = Projection(0);
        var paid = free with { EffectivePriceCoins = 25 };
        var page = new ChapterManifestPageSource(1, 1200, 1800, "chapter/page");
        var store = new FakeStore(call => new ChapterPageContentSource(
            call == 1 ? free : paid, page));

        var result = await Handler(store)
            .GetPageContentAsync(free.ChapterId, 1, null);

        Assert.Equal(ChapterManifestOutcome.AuthenticationRequired, result.Outcome);
        Assert.Null(result.Content);
        Assert.Equal(2, store.ContentCalls);
    }

    [Fact]
    public async Task Missing_hidden_or_provider_failure_never_returns_content()
    {
        var missingProvider = new FakeContentProvider();
        var missingHandler = Handler(new FakeStore(), missingProvider);

        var access = await missingHandler.GetAccessAsync(Guid.NewGuid(), null);
        var manifest = await missingHandler.GetManifestAsync(Guid.NewGuid(), null);
        var content = await missingHandler.GetPageContentAsync(Guid.NewGuid(), 1, null);

        Assert.Equal(ChapterAccessOutcome.NotFound, access.Outcome);
        Assert.Equal(ChapterManifestOutcome.NotFound, manifest.Outcome);
        Assert.Equal(ChapterManifestOutcome.NotFound, content.Outcome);
        Assert.Equal(0, missingProvider.Calls);

        var projection = Projection(0);
        var page = new ChapterManifestPageSource(1, 1200, 1800, "chapter/page");
        var provider = new FakeContentProvider(succeeds: false);
        var failed = await Handler(
                new FakeStore(_ => new ChapterPageContentSource(projection, page)), provider)
            .GetPageContentAsync(projection.ChapterId, 1, null);

        Assert.Equal(ChapterManifestOutcome.ProviderUnavailable, failed.Outcome);
        Assert.Null(failed.Content);
        Assert.Equal(1, provider.Calls);
    }

    private static ChapterAccessHandler Handler(
        FakeStore store,
        FakeContentProvider? provider = null) =>
        new(store, provider ?? new FakeContentProvider(), new FakeClock());

    private static ChapterAccessProjection Projection(
        long price,
        bool vipEligible = false,
        bool activeVip = false,
        bool unlocked = false) => new(
        Guid.NewGuid(), price, vipEligible, activeVip, unlocked);

    private sealed class FakeStore : IChapterAccessProjectionStore
    {
        private readonly IReadOnlyDictionary<Guid, ChapterAccessProjection> _projections;
        private readonly ChapterManifestSource? _manifest;
        private readonly Func<int, ChapterPageContentSource?>? _contentSource;

        internal FakeStore(params ChapterAccessProjection[] projections) =>
            _projections = projections.ToDictionary(value => value.ChapterId);

        internal FakeStore(ChapterManifestSource manifest)
        {
            _manifest = manifest;
            _projections = new Dictionary<Guid, ChapterAccessProjection>
            {
                [manifest.Access.ChapterId] = manifest.Access,
            };
        }

        internal FakeStore(Func<int, ChapterPageContentSource?> contentSource)
        {
            _contentSource = contentSource;
            _projections = new Dictionary<Guid, ChapterAccessProjection>();
        }

        internal int BatchCalls { get; private set; }
        internal int ContentCalls { get; private set; }

        public Task<IReadOnlyDictionary<Guid, ChapterAccessProjection>> GetManyAsync(
            IReadOnlyCollection<Guid> chapterIds,
            Guid? userId,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            IReadOnlyDictionary<Guid, ChapterAccessProjection> result = _projections
                .Where(pair => chapterIds.Contains(pair.Key))
                .ToDictionary();
            return Task.FromResult(result);
        }

        public Task<ChapterManifestSource?> GetManifestSourceAsync(
            Guid chapterId,
            Guid? userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_manifest?.Access.ChapterId == chapterId ? _manifest : null);

        public Task<ChapterPageContentSource?> GetPageContentSourceAsync(
            Guid chapterId,
            int pageOrder,
            Guid? userId,
            CancellationToken cancellationToken = default)
        {
            ContentCalls++;
            var source = _contentSource?.Invoke(ContentCalls);
            return Task.FromResult(
                source?.Access.ChapterId == chapterId && source.Page.Order == pageOrder
                    ? source
                    : null);
        }
    }

    private sealed class FakeContentProvider(bool succeeds = true) : IChapterPageContentProvider
    {
        internal int Calls { get; private set; }

        public Task<ChapterPageContent?> GetAsync(
            string assetKey,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ChapterPageContent?>(succeeds
                ? new ChapterPageContent([1, 2, 3], "image/webp")
                : null);
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
