using System.Data;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Interaction.Ratings;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using ComicManagement.Workflows.Interaction;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class ComicRatingPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 23, 8, 30, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_rating_tests")
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
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Upsert_update_delete_keep_summary_and_single_user_comic_row_accurate()
    {
        var (comicId, firstUserId) = await SeedPublicComicAsync();
        var secondUserId = await SeedUserAsync("rating-user-two");

        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ComicRatingHandler>();

        var first = await handler.UpsertAsync(comicId, firstUserId, 3);
        var second = await handler.UpsertAsync(comicId, secondUserId, 5);
        var updated = await handler.UpsertAsync(comicId, firstUserId, 4);

        Assert.Equal(2, updated.Summary!.RatingCount);
        Assert.Equal(4.5m, updated.Summary.AverageStars);
        Assert.Equal((short)4, updated.Summary.MyRating);
        Assert.Equal((short)3, first.Summary!.MyRating);
        Assert.Equal((short)5, second.Summary!.MyRating);

        var deleted = await handler.DeleteAsync(comicId, firstUserId);
        Assert.Equal(1, deleted.Summary!.RatingCount);
        Assert.Equal(5m, deleted.Summary.AverageStars);
        Assert.Null(deleted.Summary.MyRating);
    }

    [Fact]
    public async Task Concurrent_same_user_updates_really_contend_and_never_duplicate_rating()
    {
        var (comicId, userId) = await SeedPublicComicAsync();
        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "SELECT id FROM catalog.comics WHERE id = @id FOR UPDATE;",
            blocker,
            blockerTransaction))
        {
            command.Parameters.AddWithValue("id", comicId);
            await command.ExecuteNonQueryAsync();
        }

        async Task<ComicRatingResult> RateAsync(int stars)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<ComicRatingHandler>()
                .UpsertAsync(comicId, userId, stars);
        }

        var first = RateAsync(2);
        var second = RateAsync(5);
        await Task.Delay(250);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        await blockerTransaction.CommitAsync();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Equal(ComicRatingOutcome.Succeeded, result.Outcome));

        await using var verificationScope = _services.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await context.ComicRatings
            .Where(rating => rating.UserId == userId && rating.ComicId == comicId)
            .ToListAsync();
        var rating = Assert.Single(persisted);
        Assert.Contains(rating.Stars, new short[] { 2, 5 });
    }

    [Fact]
    public async Task Database_rejects_invalid_stars_and_foreign_keys()
    {
        var (comicId, userId) = await SeedPublicComicAsync();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var invalidStars = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO interaction.comic_ratings
                    (user_id, comic_id, stars, created_at, updated_at)
                VALUES ({userId}, {comicId}, {0}, {Now}, {Now});
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidStars.SqlState);

        var invalidUser = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO interaction.comic_ratings
                    (user_id, comic_id, stars, created_at, updated_at)
                VALUES ({Guid.NewGuid()}, {comicId}, {5}, {Now}, {Now});
                """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, invalidUser.SqlState);
    }

    [Fact]
    public async Task Draft_or_invalid_comic_is_not_exposed_and_cannot_receive_rating()
    {
        var userId = await SeedUserAsync("draft-rating-user");
        var comicId = Guid.NewGuid();
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Comics.Add(Comic.Create(
                comicId,
                "draft-rating-comic",
                "Draft Rating Comic",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now));
            await context.SaveChangesAsync();
        }

        await using var commandScope = _services.CreateAsyncScope();
        var handler = commandScope.ServiceProvider.GetRequiredService<ComicRatingHandler>();
        Assert.Equal(
            ComicRatingOutcome.ComicUnavailable,
            (await handler.UpsertAsync(comicId, userId, 5)).Outcome);
        Assert.Equal(
            ComicRatingOutcome.ComicUnavailable,
            (await handler.GetAsync(comicId, userId)).Outcome);
    }

    [Fact]
    public async Task Rating_get_uses_one_snapshot_when_comic_is_hidden_between_projection_steps()
    {
        var (comicId, userId) = await SeedPublicComicAsync();
        var observer = new BlockingRatingReadObserver();
        await using var services = BuildServices(observer, null);
        await using var scope = services.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<ComicRatingHandler>()
            .GetAsync(comicId, userId);

        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await HideComicAsync(comicId);
        observer.Release.TrySetResult();

        var result = await read;
        Assert.Equal(ComicRatingOutcome.Succeeded, result.Outcome);

        await using var verificationScope = _services.CreateAsyncScope();
        var fresh = await verificationScope.ServiceProvider
            .GetRequiredService<ComicRatingHandler>()
            .GetAsync(comicId, userId);
        Assert.Equal(ComicRatingOutcome.ComicUnavailable, fresh.Outcome);
    }

    [Fact]
    public async Task Comic_detail_uses_one_snapshot_when_comic_is_hidden_before_rating_projection()
    {
        var (comicId, userId) = await SeedPublicComicAsync();
        var observer = new BlockingComicDetailReadObserver();
        await using var services = BuildServices(null, observer);
        await using var scope = services.CreateAsyncScope();
        var workflow = scope.ServiceProvider.GetRequiredService<PublicComicDetailWorkflow>();
        var read = workflow.GetAsync($"rating-{comicId:N}", userId);

        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await HideComicAsync(comicId);
        observer.Release.TrySetResult();

        var result = await read;
        Assert.Equal(ComicManagement.Catalog.Browsing.PublicCatalogOutcome.Succeeded, result.Outcome);
        Assert.Equal(comicId, result.Detail!.Id);
        Assert.NotNull(result.Rating);

        await using var verificationScope = _services.CreateAsyncScope();
        var freshWorkflow = new PublicComicDetailWorkflow(
            verificationScope.ServiceProvider.GetRequiredService<ComicManagement.Shared.Persistence.IConsistentReadExecutor>(),
            verificationScope.ServiceProvider.GetRequiredService<ComicManagement.Catalog.Browsing.PublicCatalogHandler>(),
            verificationScope.ServiceProvider.GetRequiredService<ComicRatingHandler>(),
            new NoOpPublicComicDetailReadObserver());
        var fresh = await freshWorkflow.GetAsync($"rating-{comicId:N}", userId);
        Assert.Equal(ComicManagement.Catalog.Browsing.PublicCatalogOutcome.NotFound, fresh.Outcome);
    }

    [Fact]
    public async Task Consistent_read_executor_opens_repeatable_read_when_no_transaction_exists()
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var executor = scope.ServiceProvider
            .GetRequiredService<ComicManagement.Shared.Persistence.IConsistentReadExecutor>();

        Assert.Null(context.Database.CurrentTransaction);
        var isolationLevel = await executor.ExecuteAsync(_ => Task.FromResult(
            context.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel));

        Assert.Equal(IsolationLevel.RepeatableRead, isolationLevel);
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Consistent_read_executor_reuses_existing_repeatable_read_transaction()
    {
        await AssertConsistentTransactionIsReusedAsync(IsolationLevel.RepeatableRead);
    }

    [Fact]
    public async Task Consistent_read_executor_reuses_existing_serializable_transaction()
    {
        await AssertConsistentTransactionIsReusedAsync(IsolationLevel.Serializable);
    }

    [Fact]
    public async Task Consistent_read_executor_rejects_existing_read_committed_transaction()
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var executor = scope.ServiceProvider
            .GetRequiredService<ComicManagement.Shared.Persistence.IConsistentReadExecutor>();
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted);
        var callbackInvoked = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync<int>(_ =>
            {
                callbackInvoked = true;
                return Task.FromResult(0);
            }));

        Assert.False(callbackInvoked);
        Assert.Contains("RepeatableRead or Serializable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ReadCommitted", exception.Message, StringComparison.Ordinal);
    }

    private async Task AssertConsistentTransactionIsReusedAsync(IsolationLevel isolationLevel)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var executor = scope.ServiceProvider
            .GetRequiredService<ComicManagement.Shared.Persistence.IConsistentReadExecutor>();
        await using var transaction = await context.Database.BeginTransactionAsync(isolationLevel);

        var observedTransaction = await executor.ExecuteAsync(_ =>
            Task.FromResult(context.Database.CurrentTransaction));

        Assert.Same(transaction, observedTransaction);
        Assert.Equal(isolationLevel, transaction.GetDbTransaction().IsolationLevel);
    }

    private ServiceProvider BuildServices(
        IComicRatingReadObserver? ratingObserver,
        IPublicComicDetailReadObserver? detailObserver)
    {
        var services = new ServiceCollection().AddPersistence(_postgres.GetConnectionString());
        if (ratingObserver is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(ratingObserver));
        }

        services.AddSingleton(detailObserver ?? new NoOpPublicComicDetailReadObserver());
        services.AddScoped<PublicComicDetailWorkflow>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task HideComicAsync(Guid comicId)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hidden = ComicPublicationStatuses.Hidden;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.comics SET publication_status = {hidden} WHERE id = {comicId}");
    }

    private async Task<(Guid ComicId, Guid UserId)> SeedPublicComicAsync()
    {
        var userId = await SeedUserAsync($"rating-user-{Guid.NewGuid():N}");
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var comic = Comic.Create(
            comicId,
            $"rating-{comicId:N}",
            "Rating Comic",
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
            0,
            null,
            Now);
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            1,
            $"ratings/{comicId:N}/1",
            $"sha256:{comicId:N}{comicId:N}",
            Now);
        page.MarkReady(1200, 1800);
        chapter.Publish(true, 1, Now.AddMinutes(1));
        comic.Publish(true, 1, Now.AddMinutes(2));
        context.Comics.Add(comic);
        context.Chapters.Add(chapter);
        context.ChapterPages.Add(page);
        await context.SaveChangesAsync();
        return (comicId, userId);
    }

    private async Task<Guid> SeedUserAsync(string subject)
    {
        var userId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            subject,
            $"{subject}@example.com",
            null,
            Now));
        await context.SaveChangesAsync();
        return userId;
    }

    private sealed class BlockingRatingReadObserver : IComicRatingReadObserver
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AfterPublicVisibilityReadAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class BlockingComicDetailReadObserver : IPublicComicDetailReadObserver
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AfterCatalogReadAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
