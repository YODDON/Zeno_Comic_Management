using ComicManagement.Catalog;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Publishing;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class CatalogPublicationPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_publication_tests")
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
    public async Task Publication_workflow_rejects_invalid_state_and_preserves_first_publish_slug_lock()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await SeedAsync(comicId, chapterId, readyPage: false);

        Assert.Equal(
            CatalogPublicationOutcome.PublicationPreconditionFailed,
            (await ExecuteAsync(store => store.PublishChapterAsync(
                chapterId,
                1,
                Now))).Outcome);
        Assert.Equal(
            CatalogPublicationOutcome.PublicationPreconditionFailed,
            (await ExecuteAsync(store => store.PublishComicAsync(
                comicId,
                1,
                Now))).Outcome);

        await AddReadyPageAsync(chapterId, pageOrder: 1);

        var chapterPublish = await ExecuteAsync(store => store.PublishChapterAsync(
            chapterId,
            1,
            Now.AddMinutes(1)));
        var comicPublish = await ExecuteAsync(store => store.PublishComicAsync(
            comicId,
            1,
            Now.AddMinutes(2)));

        Assert.Equal(CatalogPublicationOutcome.Succeeded, chapterPublish.Outcome);
        Assert.Equal(CatalogPublicationOutcome.Succeeded, comicPublish.Outcome);
        Assert.Equal(Now.AddMinutes(2), comicPublish.State!.FirstPublishedAt);

        Assert.Equal(
            CatalogPublicationOutcome.PublicationPreconditionFailed,
            (await ExecuteAsync(store => store.HideChapterAsync(
                chapterId,
                2,
                Now.AddMinutes(3)))).Outcome);

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = await context.Comics.SingleAsync(value => value.Id == comicId);
            var slugLock = Assert.Throws<CatalogRuleViolationException>(() =>
                comic.ChangeSlug("cannot-change-after-publish", 2, Now.AddMinutes(3)));
            Assert.Equal(CatalogRuleViolation.SlugLocked, slugLock.Violation);
        }

        var comicHide = await ExecuteAsync(store => store.HideComicAsync(
            comicId,
            2,
            Now.AddMinutes(4)));
        var chapterHide = await ExecuteAsync(store => store.HideChapterAsync(
            chapterId,
            2,
            Now.AddMinutes(5)));

        Assert.Equal(CatalogPublicationOutcome.Succeeded, comicHide.Outcome);
        Assert.Equal(CatalogPublicationOutcome.Succeeded, chapterHide.Outcome);

        await using var verifyScope = _services.CreateAsyncScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedComic = await verifyContext.Comics.AsNoTracking()
            .SingleAsync(value => value.Id == comicId);
        Assert.Equal(ComicPublicationStatuses.Hidden, persistedComic.PublicationStatus);
        Assert.Equal(Now.AddMinutes(2), persistedComic.FirstPublishedAt);
        Assert.Equal(
            ChapterPublicationStatuses.Hidden,
            (await verifyContext.Chapters.AsNoTracking()
                .SingleAsync(value => value.Id == chapterId)).PublicationStatus);
    }

    [Fact]
    public async Task Concurrent_hides_cannot_remove_both_valid_chapters_from_published_comic()
    {
        var comicId = Guid.NewGuid();
        var firstChapterId = Guid.NewGuid();
        var secondChapterId = Guid.NewGuid();
        await SeedAsync(comicId, firstChapterId, readyPage: true);
        await SeedChapterAsync(comicId, secondChapterId, sortOrder: 2, readyPage: true);

        Assert.Equal(
            CatalogPublicationOutcome.Succeeded,
            (await ExecuteAsync(store => store.PublishChapterAsync(
                firstChapterId,
                1,
                Now.AddMinutes(1)))).Outcome);
        Assert.Equal(
            CatalogPublicationOutcome.Succeeded,
            (await ExecuteAsync(store => store.PublishChapterAsync(
                secondChapterId,
                1,
                Now.AddMinutes(1)))).Outcome);
        Assert.Equal(
            CatalogPublicationOutcome.Succeeded,
            (await ExecuteAsync(store => store.PublishComicAsync(
                comicId,
                1,
                Now.AddMinutes(2)))).Outcome);

        using var start = new ManualResetEventSlim();
        var firstHide = Task.Run(async () =>
        {
            start.Wait();
            return await ExecuteAsync(store => store.HideChapterAsync(
                firstChapterId,
                2,
                Now.AddMinutes(3)));
        });
        var secondHide = Task.Run(async () =>
        {
            start.Wait();
            return await ExecuteAsync(store => store.HideChapterAsync(
                secondChapterId,
                2,
                Now.AddMinutes(3)));
        });

        start.Set();
        var outcomes = (await Task.WhenAll(firstHide, secondHide))
            .Select(result => result.Outcome)
            .ToArray();

        Assert.Single(outcomes, outcome => outcome == CatalogPublicationOutcome.Succeeded);
        Assert.Single(
            outcomes,
            outcome => outcome == CatalogPublicationOutcome.PublicationPreconditionFailed);

        await using var verifyScope = _services.CreateAsyncScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            1,
            await verifyContext.Chapters.CountAsync(chapter =>
                chapter.ComicId == comicId &&
                chapter.PublicationStatus == ChapterPublicationStatuses.Published));
    }

    [Fact]
    public async Task Publication_store_returns_not_found_and_concurrency_conflict()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await SeedAsync(comicId, chapterId, readyPage: true);

        Assert.Equal(
            CatalogPublicationOutcome.NotFound,
            (await ExecuteAsync(store => store.PublishComicAsync(
                Guid.NewGuid(),
                1,
                Now))).Outcome);
        Assert.Equal(
            CatalogPublicationOutcome.ConcurrencyConflict,
            (await ExecuteAsync(store => store.PublishChapterAsync(
                chapterId,
                2,
                Now))).Outcome);
    }

    private async Task SeedAsync(Guid comicId, Guid chapterId, bool readyPage)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Comics.Add(Comic.Create(
            comicId,
            $"comic-{comicId:N}",
            "Publication Test",
            string.Empty,
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            Now));
        await context.SaveChangesAsync();
        await SeedChapterAsync(comicId, chapterId, sortOrder: 1, readyPage);
    }

    private async Task SeedChapterAsync(
        Guid comicId,
        Guid chapterId,
        int sortOrder,
        bool readyPage)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Chapters.Add(Chapter.Create(
            chapterId,
            comicId,
            $"Chapter {sortOrder}",
            sortOrder,
            0,
            false,
            Now));
        if (readyPage)
        {
            context.ChapterPages.Add(CreateReadyPage(chapterId, sortOrder));
        }

        await context.SaveChangesAsync();
    }

    private async Task AddReadyPageAsync(Guid chapterId, int pageOrder)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.ChapterPages.Add(CreateReadyPage(chapterId, pageOrder));
        await context.SaveChangesAsync();
    }

    private static ChapterPage CreateReadyPage(Guid chapterId, int pageOrder)
    {
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            pageOrder,
            $"publication/{chapterId:N}/{pageOrder}",
            $"sha256:{new string('a', 64)}",
            Now);
        page.MarkReady(1200, 1800);
        return page;
    }

    private async Task<CatalogPublicationResult> ExecuteAsync(
        Func<ICatalogPublicationStore, Task<CatalogPublicationResult>> command)
    {
        await using var scope = _services.CreateAsyncScope();
        return await command(scope.ServiceProvider
            .GetRequiredService<ICatalogPublicationStore>());
    }
}
