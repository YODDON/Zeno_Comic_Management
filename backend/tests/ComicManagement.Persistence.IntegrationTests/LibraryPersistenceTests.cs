using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Persistence;
using ComicManagement.Reading.Events;
using ComicManagement.Reading.Library;
using ComicManagement.Reading.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class LibraryPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 5, 30, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_library_tests")
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
    public async Task Migration_creates_follow_key_foreign_keys_and_cursor_index()
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var constraints = await context.Database.SqlQueryRaw<string>(
                "SELECT conname AS \"Value\" FROM pg_constraint " +
                "WHERE conrelid = 'reading.comic_follows'::regclass")
            .ToListAsync();
        var indexes = await context.Database.SqlQueryRaw<string>(
                "SELECT indexname AS \"Value\" FROM pg_indexes " +
                "WHERE schemaname = 'reading' AND tablename = 'comic_follows'")
            .ToListAsync();
        var progressConstraints = await context.Database.SqlQueryRaw<string>(
                "SELECT conname AS \"Value\" FROM pg_constraint " +
                "WHERE conrelid = 'reading.reading_progress'::regclass")
            .ToListAsync();

        Assert.Contains("pk_comic_follows", constraints);
        Assert.Contains("fk_comic_follows_users", constraints);
        Assert.Contains("fk_comic_follows_comics", constraints);
        Assert.Contains("ix_comic_follows_user_followed_comic", indexes);
        Assert.Contains("ck_reading_progress_accepted_progress", progressConstraints);
    }

    [Fact]
    public async Task Concurrent_follow_is_idempotent_and_writes_one_event()
    {
        var seed = await SeedPublicChapterAsync();

        var results = await Task.WhenAll(
            FollowInNewScopeAsync(seed.UserId, seed.ComicId, Now),
            FollowInNewScopeAsync(seed.UserId, seed.ComicId, Now));

        Assert.All(results, result => Assert.Equal(LibraryOutcome.Succeeded, result.Outcome));
        Assert.Single(results, result => result.State!.Created);
        Assert.Single(results, result => !result.State!.Created);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.ComicFollows.CountAsync());
        Assert.Equal(1, await context.OutboxMessages.CountAsync(message =>
            message.EventType == ReadingEventTypes.ComicFollowed));
        var status = await scope.ServiceProvider.GetRequiredService<ILibraryStore>()
            .GetFollowStatusAsync(seed.UserId, seed.ComicId);
        Assert.True(status.Followed);
        Assert.Equal(Now, status.FollowedAt);
    }

    [Fact]
    public async Task Follow_requires_public_valid_comic_and_unfollow_is_idempotent()
    {
        var seed = await SeedPublicChapterAsync();
        await using (var mutationScope = _services.CreateAsyncScope())
        {
            await mutationScope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Comics
                .Where(comic => comic.Id == seed.ComicId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    comic => comic.PublicationStatus,
                    ComicPublicationStatuses.Hidden));
        }

        var denied = await FollowInNewScopeAsync(seed.UserId, seed.ComicId, Now);
        Assert.Equal(LibraryOutcome.NotFound, denied.Outcome);

        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ILibraryStore>();
        await store.UnfollowAsync(seed.UserId, seed.ComicId);
        await store.UnfollowAsync(seed.UserId, seed.ComicId);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .ComicFollows.ToListAsync());
    }

    [Fact]
    public async Task Continue_rechecks_visibility_and_never_exposes_hidden_page_position()
    {
        var seed = await SeedPublicChapterAsync();
        await using (var sessionScope = _services.CreateAsyncScope())
        {
            var store = sessionScope.ServiceProvider.GetRequiredService<IReadingSessionStore>();
            var created = await store.CreateAsync(
                Guid.NewGuid(),
                seed.UserId,
                seed.ChapterId,
                ReadingQualificationPolicy.R02V1,
                Now);
            await store.UpdateProgressAsync(
                created.State!.SessionId,
                seed.UserId,
                1,
                0.6m,
                1,
                new string('a', 64),
                Now.AddSeconds(1));
        }

        await using (var availableScope = _services.CreateAsyncScope())
        {
            var items = await availableScope.ServiceProvider
                .GetRequiredService<ILibraryStore>()
                .ListContinueAsync(seed.UserId, null, 20);
            var item = Assert.Single(items);
            Assert.Equal(LibraryAvailability.Available, item.Availability);
            Assert.True(item.CanRead);
            Assert.Equal(1, item.PageOrder);
            Assert.Equal(0.6m, item.PageOffsetRatio);
            Assert.Equal(3_000, item.AcceptedMaxProgressBasisPoints);
        }

        await using (var pageMutationScope = _services.CreateAsyncScope())
        {
            await pageMutationScope.ServiceProvider.GetRequiredService<AppDbContext>()
                .ChapterPages
                .Where(page => page.ChapterId == seed.ChapterId && page.PageOrder == 1)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(page => page.AssetStatus, ChapterPageAssetStatuses.Pending)
                    .SetProperty(page => page.Width, 0)
                    .SetProperty(page => page.Height, 0));
        }

        await using (var remappedScope = _services.CreateAsyncScope())
        {
            var items = await remappedScope.ServiceProvider
                .GetRequiredService<ILibraryStore>()
                .ListContinueAsync(seed.UserId, null, 20);
            var item = Assert.Single(items);
            Assert.Equal(LibraryAvailability.Available, item.Availability);
            Assert.Equal(2, item.PageOrder);
            Assert.Equal(0m, item.PageOffsetRatio);
        }

        await using (var mutationScope = _services.CreateAsyncScope())
        {
            await mutationScope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Chapters
                .Where(chapter => chapter.Id == seed.ChapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PublicationStatus,
                    ChapterPublicationStatuses.Hidden));
        }

        await using var hiddenScope = _services.CreateAsyncScope();
        var hiddenItems = await hiddenScope.ServiceProvider
            .GetRequiredService<ILibraryStore>()
            .ListContinueAsync(seed.UserId, null, 20);
        var hidden = Assert.Single(hiddenItems);
        Assert.Equal(LibraryAvailability.Unavailable, hidden.Availability);
        Assert.Null(hidden.Title);
        Assert.Null(hidden.ChapterId);
        Assert.Null(hidden.PageOrder);
        Assert.Null(hidden.AcceptedMaxProgressBasisPoints);
        Assert.False(hidden.CanRead);
    }

    private async Task<FollowCommandResult> FollowInNewScopeAsync(
        Guid userId,
        Guid comicId,
        DateTimeOffset followedAt)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILibraryStore>()
            .FollowAsync(userId, comicId, followedAt);
    }

    private async Task<SeededChapter> SeedPublicChapterAsync()
    {
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            "Library User",
            null,
            null,
            Now));
        var comic = Comic.Create(
            comicId,
            $"library-{comicId:N}",
            "Library Comic",
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
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            1,
            $"reading/{chapterId:N}/1",
            $"sha256:{1:x64}",
            Now);
        page.MarkReady(1200, 1800);
        var secondPage = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            2,
            $"reading/{chapterId:N}/2",
            $"sha256:{2:x64}",
            Now);
        secondPage.MarkReady(1200, 1800);
        chapter.Publish(true, 1, Now.AddMinutes(1));
        comic.Publish(true, 1, Now.AddMinutes(2));
        context.Comics.Add(comic);
        context.Chapters.Add(chapter);
        context.ChapterPages.Add(page);
        context.ChapterPages.Add(secondPage);
        await context.SaveChangesAsync();
        return new SeededChapter(userId, comicId, chapterId);
    }

    private sealed record SeededChapter(Guid UserId, Guid ComicId, Guid ChapterId);
}
