using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Publishing;
using ComicManagement.Persistence;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class CatalogPublicationPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);
    private static readonly Guid ActorId = Guid.NewGuid();

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_publication_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

    private readonly MutableClock _clock = new(Now);
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = new ServiceCollection()
            .AddSingleton<IClock>(_clock)
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
    public async Task Publication_workflow_rejects_invalid_state_and_writes_atomic_audit_records()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await SeedAsync(comicId, chapterId, readyPage: false);

        Assert.Equal(
            CatalogPublicationOutcome.PublicationPreconditionFailed,
            (await ExecuteAsync(handler => handler.PublishChapterAsync(
                chapterId,
                1,
                ActorId,
                "rejected-chapter"))).Outcome);
        Assert.Equal(
            CatalogPublicationOutcome.PublicationPreconditionFailed,
            (await ExecuteAsync(handler => handler.PublishComicAsync(
                comicId,
                1,
                ActorId,
                "rejected-comic"))).Outcome);

        await AddReadyPageAsync(chapterId, pageOrder: 1);

        var chapterPublish = await ExecuteAtAsync(
            Now.AddMinutes(1),
            handler => handler.PublishChapterAsync(
                chapterId,
                1,
                ActorId,
                "publish-chapter"));
        var comicPublish = await ExecuteAtAsync(
            Now.AddMinutes(2),
            handler => handler.PublishComicAsync(
                comicId,
                1,
                ActorId,
                "publish-comic"));

        Assert.Equal(CatalogPublicationOutcome.Succeeded, chapterPublish.Outcome);
        Assert.Equal(CatalogPublicationOutcome.Succeeded, comicPublish.Outcome);
        Assert.Equal(Now.AddMinutes(2), comicPublish.State!.FirstPublishedAt);

        Assert.Equal(
            CatalogPublicationOutcome.PublicationPreconditionFailed,
            (await ExecuteAtAsync(
                Now.AddMinutes(3),
                handler => handler.HideChapterAsync(
                    chapterId,
                    2,
                    ActorId,
                    "rejected-hide"))).Outcome);
        Assert.Equal(
            CatalogPublicationOutcome.PublicationPreconditionFailed,
            (await ExecuteAtAsync(
                Now.AddMinutes(3),
                handler => handler.ChangeComicSlugAsync(
                    comicId,
                    "cannot-change-after-publish",
                    2,
                    ActorId,
                    "rejected-slug"))).Outcome);

        var comicHide = await ExecuteAtAsync(
            Now.AddMinutes(4),
            handler => handler.HideComicAsync(
                comicId,
                2,
                ActorId,
                "hide-comic"));
        var chapterHide = await ExecuteAtAsync(
            Now.AddMinutes(5),
            handler => handler.HideChapterAsync(
                chapterId,
                2,
                ActorId,
                "hide-chapter"));

        Assert.Equal(CatalogPublicationOutcome.Succeeded, comicHide.Outcome);
        Assert.Equal(CatalogPublicationOutcome.Succeeded, chapterHide.Outcome);

        await using var verifyScope = _services.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var comic = await context.Comics.AsNoTracking().SingleAsync(value => value.Id == comicId);
        Assert.Equal(ComicPublicationStatuses.Hidden, comic.PublicationStatus);
        Assert.Equal(Now.AddMinutes(2), comic.FirstPublishedAt);
        Assert.Equal(
            ChapterPublicationStatuses.Hidden,
            (await context.Chapters.AsNoTracking()
                .SingleAsync(value => value.Id == chapterId)).PublicationStatus);

        var audits = await context.AuditLogs.AsNoTracking()
            .OrderBy(audit => audit.OccurredAt)
            .ToListAsync();
        Assert.Equal(4, audits.Count);
        Assert.All(audits, audit => Assert.Equal(ActorId, audit.ActorId));
        Assert.Equal(
            [
                "Catalog.ChapterPublished",
                "Catalog.ComicPublished",
                "Catalog.ComicHidden",
                "Catalog.ChapterHidden",
            ],
            audits.Select(audit => audit.Action));
        Assert.DoesNotContain(audits, audit => audit.CorrelationId.StartsWith(
            "rejected-",
            StringComparison.Ordinal));

        var immutable = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE administration.audit_logs SET action = 'changed' WHERE id = {audits[0].Id}"));
        Assert.Equal("23000", immutable.SqlState);
    }

    [Fact]
    public async Task Concurrent_hides_wait_on_the_same_comic_lock_and_preserve_one_valid_chapter()
    {
        var comicId = Guid.NewGuid();
        var firstChapterId = Guid.NewGuid();
        var secondChapterId = Guid.NewGuid();
        await SeedAsync(comicId, firstChapterId, readyPage: true);
        await SeedChapterAsync(comicId, secondChapterId, sortOrder: 2, readyPage: true);

        await ExecuteAsync(handler => handler.PublishChapterAsync(
            firstChapterId, 1, ActorId, "publish-first"));
        await ExecuteAsync(handler => handler.PublishChapterAsync(
            secondChapterId, 1, ActorId, "publish-second"));
        await ExecuteAsync(handler => handler.PublishComicAsync(
            comicId, 1, ActorId, "publish-parent"));

        await using var blocker = await HoldComicLockAsync(comicId);
        var firstHide = ExecuteAsync(handler => handler.HideChapterAsync(
            firstChapterId, 2, ActorId, "hide-first"));
        var secondHide = ExecuteAsync(handler => handler.HideChapterAsync(
            secondChapterId, 2, ActorId, "hide-second"));

        await WaitForRowLockWaitersAsync(expectedWaiters: 2);
        await blocker.Transaction.CommitAsync();
        var outcomes = (await Task.WhenAll(firstHide, secondHide))
            .Select(result => result.Outcome)
            .ToArray();

        Assert.Single(outcomes, outcome => outcome == CatalogPublicationOutcome.Succeeded);
        Assert.Single(
            outcomes,
            outcome => outcome == CatalogPublicationOutcome.PublicationPreconditionFailed);

        await using var verifyScope = _services.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            1,
            await context.Chapters.CountAsync(chapter =>
                chapter.ComicId == comicId &&
                chapter.PublicationStatus == ChapterPublicationStatuses.Published));
    }

    [Fact]
    public async Task Publish_comic_racing_last_chapter_hide_allows_only_one_command()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await SeedAsync(comicId, chapterId, readyPage: true);
        await ExecuteAsync(handler => handler.PublishChapterAsync(
            chapterId, 1, ActorId, "publish-chapter-race"));

        await using var blocker = await HoldComicLockAsync(comicId);
        var publish = ExecuteAsync(handler => handler.PublishComicAsync(
            comicId, 1, ActorId, "publish-comic-race"));
        var hide = ExecuteAsync(handler => handler.HideChapterAsync(
            chapterId, 2, ActorId, "hide-chapter-race"));

        await WaitForRowLockWaitersAsync(expectedWaiters: 2);
        await blocker.Transaction.CommitAsync();
        var outcomes = (await Task.WhenAll(publish, hide)).Select(result => result.Outcome);

        Assert.Single(outcomes, outcome => outcome == CatalogPublicationOutcome.Succeeded);
        Assert.Single(
            outcomes,
            outcome => outcome == CatalogPublicationOutcome.PublicationPreconditionFailed);
    }

    [Fact]
    public async Task First_publish_racing_slug_change_serializes_and_loser_gets_concurrency_conflict()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await SeedAsync(comicId, chapterId, readyPage: true);
        await ExecuteAsync(handler => handler.PublishChapterAsync(
            chapterId, 1, ActorId, "publish-chapter-slug-race"));

        await using var blocker = await HoldComicLockAsync(comicId);
        var publish = ExecuteAsync(handler => handler.PublishComicAsync(
            comicId, 1, ActorId, "publish-comic-slug-race"));
        var changeSlug = ExecuteAsync(handler => handler.ChangeComicSlugAsync(
            comicId,
            "race-renamed-comic",
            1,
            ActorId,
            "change-slug-race"));

        await WaitForRowLockWaitersAsync(expectedWaiters: 2);
        await blocker.Transaction.CommitAsync();
        var outcomes = (await Task.WhenAll(publish, changeSlug)).Select(result => result.Outcome);

        Assert.Single(outcomes, outcome => outcome == CatalogPublicationOutcome.Succeeded);
        Assert.Single(outcomes, outcome => outcome == CatalogPublicationOutcome.ConcurrencyConflict);

        await using var verifyScope = _services.CreateAsyncScope();
        var comic = await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Comics.AsNoTracking()
            .SingleAsync(value => value.Id == comicId);
        Assert.Equal(2, comic.Version);
        if (comic.PublicationStatus == ComicPublicationStatuses.Published)
        {
            Assert.NotNull(comic.FirstPublishedAt);
            Assert.NotEqual("race-renamed-comic", comic.Slug);
        }
        else
        {
            Assert.Null(comic.FirstPublishedAt);
            Assert.Equal("race-renamed-comic", comic.Slug);
        }
    }

    [Fact]
    public async Task Slug_change_uses_production_unique_constraint_and_returns_conflict()
    {
        var firstComicId = Guid.NewGuid();
        var secondComicId = Guid.NewGuid();
        await SeedAsync(firstComicId, Guid.NewGuid(), readyPage: false);
        await SeedAsync(secondComicId, Guid.NewGuid(), readyPage: false);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var firstSlug = await context.Comics.AsNoTracking()
            .Where(comic => comic.Id == firstComicId)
            .Select(comic => comic.Slug)
            .SingleAsync();

        var result = await ExecuteAsync(handler => handler.ChangeComicSlugAsync(
            secondComicId,
            firstSlug,
            1,
            ActorId,
            "duplicate-slug"));

        Assert.Equal(CatalogPublicationOutcome.SlugConflict, result.Outcome);
        Assert.False(await context.AuditLogs.AsNoTracking()
            .AnyAsync(audit => audit.CorrelationId == "duplicate-slug"));
    }

    [Fact]
    public async Task Publication_store_returns_not_found_and_concurrency_conflict()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await SeedAsync(comicId, chapterId, readyPage: true);

        Assert.Equal(
            CatalogPublicationOutcome.NotFound,
            (await ExecuteAsync(handler => handler.PublishComicAsync(
                Guid.NewGuid(), 1, ActorId, "missing"))).Outcome);
        Assert.Equal(
            CatalogPublicationOutcome.ConcurrencyConflict,
            (await ExecuteAsync(handler => handler.PublishChapterAsync(
                chapterId, 2, ActorId, "stale"))).Outcome);
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
        Func<CatalogPublicationHandler, Task<CatalogPublicationResult>> command)
    {
        await using var scope = _services.CreateAsyncScope();
        return await command(scope.ServiceProvider
            .GetRequiredService<CatalogPublicationHandler>());
    }

    private Task<CatalogPublicationResult> ExecuteAtAsync(
        DateTimeOffset occurredAt,
        Func<CatalogPublicationHandler, Task<CatalogPublicationResult>> command)
    {
        _clock.UtcNow = occurredAt;
        return ExecuteAsync(command);
    }

    private async Task<ComicLockBlocker> HoldComicLockAsync(Guid comicId)
    {
        var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM catalog.comics WHERE id = @comic_id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("comic_id", comicId);
        await command.ExecuteScalarAsync();
        return new ComicLockBlocker(connection, transaction);
    }

    private async Task WaitForRowLockWaitersAsync(int expectedWaiters)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        const int maximumAttempts = 100;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity " +
                "WHERE datname = current_database() AND wait_event_type = 'Lock';",
                connection);
            var waitingCount = Convert.ToInt32(
                await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
            if (waitingCount >= expectedWaiters)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(
            $"Expected {expectedWaiters} PostgreSQL row-lock waiters were not observed.");
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class ComicLockBlocker(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
