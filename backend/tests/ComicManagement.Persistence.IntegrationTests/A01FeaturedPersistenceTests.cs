using ComicManagement.Catalog.Featured;
using ComicManagement.Catalog.Administration;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class A01FeaturedPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("a01_featured_tests").WithUsername("postgres").WithPassword("postgres").Build();
    private ServiceProvider _services = null!;
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _comicA = Guid.NewGuid();
    private readonly Guid _comicB = Guid.NewGuid();
    private readonly Guid _chapterA = Guid.NewGuid();
    private readonly Guid _chapterB = Guid.NewGuid();
    private readonly Guid _pageA = Guid.NewGuid();
    private readonly Guid _pageB = Guid.NewGuid();
    private readonly Guid _genre = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = new ServiceCollection().AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes:true);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO identity.users (id, display_name, email, avatar_url, role, status, version, created_at, updated_at)
            VALUES ({{_actor}}, 'Admin', 'admin@a01.test', NULL, 'Admin', 'Active', 1, now(), now());
            INSERT INTO catalog.comics (id, slug, title, description, series_status, publication_status,
                first_published_at, cover_asset_id, default_price_coins, default_vip_eligible, version, created_at, updated_at)
            VALUES ({{_comicA}}, 'a01-a', 'A', '', 'Ongoing', 'Published', now(), NULL, 0, false, 1, now(), now()),
                   ({{_comicB}}, 'a01-b', 'B', '', 'Ongoing', 'Published', now(), NULL, 0, false, 1, now(), now());
            INSERT INTO catalog.chapters (id, comic_id, chapter_label, sort_order, publication_status,
                published_at, version, created_at, updated_at)
            VALUES ({{_chapterA}}, {{_comicA}}, '1', 1, 'Published', now(), 1, now(), now()),
                   ({{_chapterB}}, {{_comicB}}, '1', 1, 'Published', now(), 1, now(), now());
            INSERT INTO catalog.chapter_pages (id, chapter_id, page_order, cloudinary_public_id,
                asset_status, width, height, upload_fingerprint, upload_created_at)
            VALUES ({{_pageA}}, {{_chapterA}}, 1, 'a01/a', 'Ready', 800, 1200, 'sha256:' || repeat('a', 64), now()),
                   ({{_pageB}}, {{_chapterB}}, 1, 'a01/b', 'Ready', 800, 1200, 'sha256:' || repeat('b', 64), now());
            INSERT INTO catalog.genres (id, slug, name) VALUES ({{_genre}}, 'a01-race', 'A01 Race');
            """);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_reorders_really_contend_and_only_one_expected_version_wins()
    {
        var gate = new BlockingFeaturedObserver();
        await using var services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .AddSingleton<IFeaturedCatalogCriticalSectionObserver>(gate)
            .BuildServiceProvider(validateScopes: true);
        await using var scopeA = services.CreateAsyncScope();
        await using var scopeB = services.CreateAsyncScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IFeaturedCatalogStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IFeaturedCatalogStore>();
        var now = DateTimeOffset.UtcNow;

        var first = storeA.SaveAsync([_comicA, _comicB], 1, _actor, "a", now, default);
        await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var second = storeB.SaveAsync([_comicB, _comicA], 1, _actor, "b", now, default);
        await Task.Delay(150);
        Assert.False(second.IsCompleted);

        gate.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Outcome == FeaturedSaveOutcome.Succeeded);
        Assert.Single(results, result => result.Outcome == FeaturedSaveOutcome.Conflict);
        var success = Assert.Single(results, result => result.Outcome == FeaturedSaveOutcome.Succeeded);
        Assert.Equal(2, success.State!.Version);
        Assert.Equal([_comicA, _comicB], success.State.ComicIds);
    }

    [Fact]
    public async Task Database_rejects_sixth_position()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO catalog.featured_comics (comic_id, display_order)
                VALUES ({{_comicA}}, 6);
                """));
        Assert.Equal("23514", exception.SqlState);
    }

    [Fact]
    public async Task Featured_save_waits_for_concurrent_hide_then_fails_closed()
    {
        var signal = new SignalingFeaturedObserver();
        await using var services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .AddSingleton<IFeaturedCatalogCriticalSectionObserver>(signal)
            .BuildServiceProvider(validateScopes: true);
        await using var blockerScope = services.CreateAsyncScope();
        await using var requestScope = services.CreateAsyncScope();
        var blocker = blockerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM catalog.comics WHERE id = {_comicA} FOR UPDATE");

        var save = requestScope.ServiceProvider.GetRequiredService<IFeaturedCatalogStore>()
            .SaveAsync([_comicA], 1, _actor, "hide-race", DateTimeOffset.MinValue, default);
        await signal.ConfigurationLocked.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        Assert.False(save.IsCompleted);

        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.comics SET publication_status = 'Hidden', version = version + 1 WHERE id = {_comicA}");
        await blockerTransaction.CommitAsync();

        var result = await save;
        Assert.Equal(FeaturedSaveOutcome.Unpublished, result.Outcome);
        await using var verificationScope = services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await verification.FeaturedComics.ToListAsync());
        Assert.Equal(1, await verification.FeaturedConfigurations.Select(x => x.Version).SingleAsync());
    }

    [Fact]
    public async Task Public_featured_reuses_canonical_visibility_for_legacy_drift()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE catalog.comics SET first_published_at = NULL WHERE id = {{_comicB}};
            INSERT INTO catalog.featured_comics (comic_id, display_order) VALUES ({{_comicB}}, 1);
            """);

        var publicItems = await scope.ServiceProvider.GetRequiredService<IFeaturedCatalogStore>()
            .GetPublicAsync(default);
        Assert.Empty(publicItems);
        Assert.False(await PublicComicVisibilityQuery.Apply(db).AnyAsync(x => x.Id == _comicB));
    }

    [Fact]
    public async Task Concurrent_comic_updates_really_contend_and_return_the_winning_command_state()
    {
        var gate = new BlockingCatalogObserver("Comic", _comicA);
        await using var services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .AddSingleton<ICatalogAdministrationCriticalSectionObserver>(gate)
            .BuildServiceProvider(validateScopes: true);
        await using var scopeA = services.CreateAsyncScope();
        await using var scopeB = services.CreateAsyncScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<ICatalogAdministrationStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<ICatalogAdministrationStore>();
        var firstInput = ComicInput(_comicA, "Winner", 1);
        var secondInput = ComicInput(_comicA, "Loser", 1);

        var first = storeA.SaveComicAsync(firstInput, _actor, "comic-a", DateTimeOffset.MinValue, default);
        await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var second = storeB.SaveComicAsync(secondInput, _actor, "comic-b", DateTimeOffset.MinValue, default);
        await Task.Delay(150);
        Assert.False(second.IsCompleted);

        gate.Release();
        var results = await Task.WhenAll(first, second);
        var success = Assert.Single(results, result => result.Outcome == CatalogAdministrationOutcome.Succeeded);
        Assert.Single(results, result => result.Outcome == CatalogAdministrationOutcome.Conflict);
        Assert.Equal("Winner", success.Value!.Title);
        Assert.Equal(2, success.Value.Version);
    }

    [Fact]
    public async Task Comic_save_waits_for_taxonomy_delete_and_returns_validation_instead_of_500()
    {
        await using var blockerScope = _services.CreateAsyncScope();
        await using var requestScope = _services.CreateAsyncScope();
        var blocker = blockerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM catalog.genres WHERE id = {_genre} FOR UPDATE");
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM catalog.genres WHERE id = {_genre}");

        var input = ComicInput(_comicB, "Taxonomy race", 1) with { GenreIds = [_genre] };
        var save = requestScope.ServiceProvider.GetRequiredService<ICatalogAdministrationStore>()
            .SaveComicAsync(input, _actor, "taxonomy-race", DateTimeOffset.MinValue, default);
        await Task.Delay(150);
        Assert.False(save.IsCompleted);

        await transaction.CommitAsync();
        var result = await save;
        Assert.Equal(CatalogAdministrationOutcome.ValidationFailed, result.Outcome);
        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verification.ComicGenres.AnyAsync(x => x.ComicId == _comicB));
    }

    [Fact]
    public async Task Comic_editor_reads_metadata_and_taxonomy_links_from_one_snapshot()
    {
        var gate = new ComicReadGate();
        await using var services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .AddSingleton<ICatalogAdministrationCriticalSectionObserver>(gate)
            .BuildServiceProvider(validateScopes: true);
        await using var readScope = services.CreateAsyncScope();
        var read = readScope.ServiceProvider.GetRequiredService<ICatalogAdministrationStore>()
            .GetComicAsync(_comicA, default);
        await gate.RootRead.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var mutationScope = services.CreateAsyncScope())
        {
            var mutation = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await mutation.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO catalog.comic_genres (comic_id, genre_id) VALUES ({_comicA}, {_genre})");
        }
        gate.Release();

        var snapshot = await read;
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.GenreIds);
    }

    private static SaveComicInput ComicInput(Guid id, string title, long version) => new(
        id, "a01-a", title, string.Empty, "Ongoing", null, 0, false, version, [], [], []);

    private sealed class BlockingFeaturedObserver : IFeaturedCatalogCriticalSectionObserver
    {
        private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstEntered => _firstEntered.Task;

        public async Task AfterConfigurationLockedAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) != 1) return;
            _firstEntered.TrySetResult();
            await _release.Task.WaitAsync(token);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class SignalingFeaturedObserver : IFeaturedCatalogCriticalSectionObserver
    {
        private readonly TaskCompletionSource _locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConfigurationLocked => _locked.Task;
        public Task AfterConfigurationLockedAsync(CancellationToken token)
        {
            _locked.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ComicReadGate : ICatalogAdministrationCriticalSectionObserver
    {
        private readonly TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RootRead => _read.Task;
        public Task AfterResourceLockedAsync(string resourceType, Guid resourceId, CancellationToken token) =>
            Task.CompletedTask;
        public async Task AfterComicRootsReadAsync(CancellationToken token)
        {
            _read.TrySetResult();
            await _release.Task.WaitAsync(token);
        }
        public void Release() => _release.TrySetResult();
    }

    private sealed class BlockingCatalogObserver(string resourceType, Guid resourceId)
        : ICatalogAdministrationCriticalSectionObserver
    {
        private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public Task FirstEntered => _firstEntered.Task;

        public async Task AfterResourceLockedAsync(string type, Guid id, CancellationToken token)
        {
            if (type != resourceType || id != resourceId || Interlocked.Increment(ref _calls) != 1) return;
            _firstEntered.TrySetResult();
            await _release.Task.WaitAsync(token);
        }

        public void Release() => _release.TrySetResult();
    }
}
