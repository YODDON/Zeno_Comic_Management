using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Persistence;
using ComicManagement.Reading.Access;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class ChapterAccessProjectionPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_reading_access_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Batch_projection_returns_only_public_valid_chapters_with_inherited_rules()
    {
        var comicId = Guid.NewGuid();
        var freeChapterId = Guid.NewGuid();
        var inheritedChapterId = Guid.NewGuid();
        var hiddenChapterId = Guid.NewGuid();
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"r01-access-{comicId:N}",
                "R01 Access",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                25,
                true,
                Now);
            AddPublishedChapter(context, comicId, freeChapterId, 1, 0, false);
            AddPublishedChapter(context, comicId, inheritedChapterId, 2, null, null);
            var hidden = AddPublishedChapter(
                context,
                comicId,
                hiddenChapterId,
                3,
                40,
                true);
            hidden.Hide(canHide: true, expectedVersion: 2, Now.AddMinutes(3));
            comic.Publish(true, 1, Now.AddMinutes(4));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        await using var queryScope = _services.CreateAsyncScope();
        var store = queryScope.ServiceProvider
            .GetRequiredService<IChapterAccessProjectionStore>();

        var projections = await store.GetManyAsync(
            [freeChapterId, inheritedChapterId, hiddenChapterId],
            Guid.NewGuid());

        Assert.Equal(2, projections.Count);
        Assert.Equal(0, projections[freeChapterId].EffectivePriceCoins);
        Assert.False(projections[freeChapterId].VipEligible);
        Assert.Equal(25, projections[inheritedChapterId].EffectivePriceCoins);
        Assert.True(projections[inheritedChapterId].VipEligible);
        Assert.False(projections[inheritedChapterId].HasActiveVip);
        Assert.False(projections[inheritedChapterId].HasCoinUnlock);
        Assert.DoesNotContain(hiddenChapterId, projections.Keys);
    }

    [Fact]
    public async Task Manifest_source_rechecks_publication_and_returns_only_ready_pages_in_order()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"r01-manifest-{comicId:N}",
                "R01 Manifest",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now);
            var chapter = Chapter.Create(
                chapterId,
                comicId,
                "Chapter 1",
                1,
                null,
                null,
                Now);
            AddReadyPage(context, chapterId, 2, "r01/page-2");
            AddReadyPage(context, chapterId, 1, "r01/page-1");
            context.ChapterPages.Add(ChapterPage.CreatePending(
                Guid.NewGuid(),
                chapterId,
                3,
                "r01/pending-page",
                $"sha256:{3:x64}",
                Now));
            chapter.Publish(true, 1, Now.AddMinutes(1));
            comic.Publish(true, 1, Now.AddMinutes(2));
            context.Chapters.Add(chapter);
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        await using (var queryScope = _services.CreateAsyncScope())
        {
            var store = queryScope.ServiceProvider
                .GetRequiredService<IChapterAccessProjectionStore>();
            var source = await store.GetManifestSourceAsync(chapterId, null);

            Assert.NotNull(source);
            Assert.Equal([1, 2], source.Pages.Select(page => page.Order));
            Assert.Equal(["r01/page-1", "r01/page-2"],
                source.Pages.Select(page => page.AssetKey));
        }

        await using (var hideScope = _services.CreateAsyncScope())
        {
            var context = hideScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Chapters
                .Where(chapter => chapter.Id == chapterId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        chapter => chapter.PublicationStatus,
                        ChapterPublicationStatuses.Hidden));
        }

        await using var finalScope = _services.CreateAsyncScope();
        var finalStore = finalScope.ServiceProvider
            .GetRequiredService<IChapterAccessProjectionStore>();
        Assert.Null(await finalStore.GetManifestSourceAsync(chapterId, null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Page_delivery_rechecks_postgresql_after_concurrent_hide_or_reprice(
        bool hideChapter)
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var seedScope = _services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"r01-content-race-{comicId:N}",
                "R01 Content Race",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now);
            AddPublishedChapter(context, comicId, chapterId, 1, 0, false);
            comic.Publish(true, 1, Now.AddMinutes(2));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        await using var readScope = _services.CreateAsyncScope();
        var provider = new BlockingContentProvider();
        var handler = new ChapterAccessHandler(
            readScope.ServiceProvider.GetRequiredService<IChapterAccessProjectionStore>(),
            provider,
            new StubClock(Now));
        var delivery = handler.GetPageContentAsync(chapterId, 1, null);
        await provider.Entered;

        await using (var mutationScope = _services.CreateAsyncScope())
        {
            var context = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (hideChapter)
            {
                await context.Chapters
                    .Where(chapter => chapter.Id == chapterId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        chapter => chapter.PublicationStatus,
                        ChapterPublicationStatuses.Hidden));
            }
            else
            {
                await context.Chapters
                    .Where(chapter => chapter.Id == chapterId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        chapter => chapter.PriceOverrideCoins,
                        (long?)25));
            }
        }

        provider.Release();
        var result = await delivery;

        Assert.Equal(
            hideChapter
                ? ChapterManifestOutcome.NotFound
                : ChapterManifestOutcome.AuthenticationRequired,
            result.Outcome);
        Assert.Null(result.Content);
    }

    private static Chapter AddPublishedChapter(
        AppDbContext context,
        Guid comicId,
        Guid chapterId,
        int sortOrder,
        long? priceOverride,
        bool? vipOverride)
    {
        var chapter = Chapter.Create(
            chapterId,
            comicId,
            $"Chapter {sortOrder}",
            sortOrder,
            priceOverride,
            vipOverride,
            Now);
        AddReadyPage(context, chapterId, 1, $"r01/{chapterId:N}/1");
        chapter.Publish(true, 1, Now.AddMinutes(sortOrder));
        context.Chapters.Add(chapter);
        return chapter;
    }

    private static void AddReadyPage(
        AppDbContext context,
        Guid chapterId,
        int pageOrder,
        string publicId)
    {
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            pageOrder,
            publicId,
            $"sha256:{pageOrder:x64}",
            Now);
        page.MarkReady(1200, 1800);
        context.ChapterPages.Add(page);
    }

    private sealed class BlockingContentProvider : IChapterPageContentProvider
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        public async Task<ChapterPageContent?> GetAsync(
            string assetKey,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new ChapterPageContent([1, 2, 3], "image/webp");
        }
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
