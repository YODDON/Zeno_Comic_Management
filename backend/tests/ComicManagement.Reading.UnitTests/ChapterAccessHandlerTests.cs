using ComicManagement.Reading.Access;
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
        var handler = new ChapterAccessHandler(store, new FakeDeliveryProvider());
        var userId = Guid.NewGuid();

        Assert.Equal(
            ChapterAccessTypes.Free,
            (await handler.GetAccessAsync(free.ChapterId, userId)).Access!.AccessType);
        Assert.Equal(
            ChapterAccessTypes.VipIncluded,
            (await handler.GetAccessAsync(vip.ChapterId, userId)).Access!.AccessType);
        Assert.Equal(
            ChapterAccessTypes.CoinUnlocked,
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
        var handler = new ChapterAccessHandler(
            new FakeStore(projection),
            new FakeDeliveryProvider());

        var result = await handler.GetAccessAsync(projection.ChapterId, null);

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
        var handler = new ChapterAccessHandler(store, new FakeDeliveryProvider());

        var decisions = await handler.GetAccessBatchAsync(
            [free.ChapterId, locked.ChapterId, missingId],
            null);

        Assert.Equal(2, decisions.Count);
        Assert.Equal(1, store.BatchCalls);
        Assert.Equal(ChapterAccessTypes.Free, decisions[free.ChapterId].AccessType);
        Assert.Equal(ChapterAccessTypes.Locked, decisions[locked.ChapterId].AccessType);
        Assert.DoesNotContain(missingId, decisions.Keys);
    }

    [Fact]
    public async Task Free_manifest_returns_ordered_delivery_pages()
    {
        var projection = Projection(0);
        var source = new ChapterManifestSource(
            projection,
            [
                new ChapterManifestPageSource(1, 1200, 1800, "chapter/page-1"),
                new ChapterManifestPageSource(2, 1000, 1500, "chapter/page-2"),
            ]);
        var provider = new FakeDeliveryProvider();
        var handler = new ChapterAccessHandler(new FakeStore(source), provider);

        var result = await handler.GetManifestAsync(projection.ChapterId, null, Now);

        Assert.Equal(ChapterManifestOutcome.Succeeded, result.Outcome);
        Assert.Equal([1, 2], result.Manifest!.Pages.Select(page => page.Order));
        Assert.All(result.Manifest.Pages, page =>
            Assert.StartsWith("https://media.example/", page.DeliveryUrl));
        Assert.Equal(Now, result.Manifest.ManifestIssuedAt);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Manifest_denies_locked_and_fails_secure_for_protected_entitlement()
    {
        var locked = Projection(25);
        var vip = Projection(25, vipEligible: true, activeVip: true);
        var page = new ChapterManifestPageSource(1, 1200, 1800, "chapter/page");
        var provider = new FakeDeliveryProvider();

        var anonymousResult = await new ChapterAccessHandler(
                new FakeStore(new ChapterManifestSource(locked, [page])),
                provider)
            .GetManifestAsync(locked.ChapterId, null, Now);
        var authenticatedResult = await new ChapterAccessHandler(
                new FakeStore(new ChapterManifestSource(locked, [page])),
                provider)
            .GetManifestAsync(locked.ChapterId, Guid.NewGuid(), Now);
        var protectedResult = await new ChapterAccessHandler(
                new FakeStore(new ChapterManifestSource(vip, [page])),
                provider)
            .GetManifestAsync(vip.ChapterId, Guid.NewGuid(), Now);

        Assert.Equal(ChapterManifestOutcome.AuthenticationRequired, anonymousResult.Outcome);
        Assert.Equal(ChapterManifestOutcome.Forbidden, authenticatedResult.Outcome);
        Assert.Equal(
            ChapterManifestOutcome.ProtectedDeliveryUnavailable,
            protectedResult.Outcome);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Missing_or_hidden_chapter_returns_not_found_without_delivery_attempt()
    {
        var provider = new FakeDeliveryProvider();
        var handler = new ChapterAccessHandler(new FakeStore(), provider);

        var access = await handler.GetAccessAsync(Guid.NewGuid(), null);
        var manifest = await handler.GetManifestAsync(Guid.NewGuid(), null, Now);

        Assert.Equal(ChapterAccessOutcome.NotFound, access.Outcome);
        Assert.Equal(ChapterManifestOutcome.NotFound, manifest.Outcome);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Free_manifest_fails_closed_when_delivery_url_cannot_be_created()
    {
        var projection = Projection(0);
        var source = new ChapterManifestSource(
            projection,
            [new ChapterManifestPageSource(1, 1200, 1800, "chapter/page")]);
        var provider = new FakeDeliveryProvider(succeeds: false);
        var handler = new ChapterAccessHandler(new FakeStore(source), provider);

        var result = await handler.GetManifestAsync(projection.ChapterId, null, Now);

        Assert.Equal(ChapterManifestOutcome.ProviderUnavailable, result.Outcome);
        Assert.Null(result.Manifest);
        Assert.Equal(1, provider.Calls);
    }

    private static ChapterAccessProjection Projection(
        long price,
        bool vipEligible = false,
        bool activeVip = false,
        bool unlocked = false) => new(
        Guid.NewGuid(),
        price,
        vipEligible,
        activeVip,
        unlocked);

    private sealed class FakeStore : IChapterAccessProjectionStore
    {
        private readonly IReadOnlyDictionary<Guid, ChapterAccessProjection> _projections;
        private readonly ChapterManifestSource? _manifest;

        internal FakeStore(params ChapterAccessProjection[] projections)
        {
            _projections = projections.ToDictionary(value => value.ChapterId);
        }

        internal FakeStore(ChapterManifestSource manifest)
        {
            _manifest = manifest;
            _projections = new Dictionary<Guid, ChapterAccessProjection>
            {
                [manifest.Access.ChapterId] = manifest.Access,
            };
        }

        internal int BatchCalls { get; private set; }

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
    }

    private sealed class FakeDeliveryProvider : IChapterPageDeliveryUrlProvider
    {
        private readonly bool _succeeds;

        internal FakeDeliveryProvider(bool succeeds = true)
        {
            _succeeds = succeeds;
        }

        internal int Calls { get; private set; }

        public bool TryCreateFreeDeliveryUrl(string assetKey, out string deliveryUrl)
        {
            Calls++;
            deliveryUrl = $"https://media.example/{assetKey}";
            return _succeeds;
        }
    }
}
