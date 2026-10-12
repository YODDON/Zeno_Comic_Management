using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Media;
using ComicManagement.Persistence;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class ChapterPersistenceTests : IAsyncLifetime
{
    private const string UploadFingerprint = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset UploadCreatedAt =
        new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_chapter_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task PostgreSql_enforces_chapter_page_checks_uniqueness_foreign_keys_and_restricts_deletes()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();

        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

        context.Comics.Add(Comic.Create(
            comicId,
            "chapter-schema",
            "Chapter Schema",
            "",
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            now));
        context.Chapters.Add(Chapter.Create(chapterId, comicId, "Chapter 1", 1, null, null, now));
        var page = ChapterPage.CreatePending(
            pageId,
            chapterId,
            1,
            "comic-management/chapter/page-1",
            UploadFingerprint,
            UploadCreatedAt);
        page.MarkReady(1200, 1800);
        context.ChapterPages.Add(page);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Chapters.CountAsync());
        Assert.Equal(1, await context.ChapterPages.CountAsync());

        await AssertPostgresViolation(
            PostgresErrorCodes.UniqueViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, "Duplicate Order", 1, 0, "Draft", 1, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, "Negative Price", 2, -1, "Draft", 1, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, "Invalid Status", 2, null, "Archived", 1, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, " ", 2, null, "Draft", 1, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, "Invalid Version", 2, null, "Draft", 0, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.ForeignKeyViolation,
            () => InsertChapter(context, Guid.NewGuid(), Guid.NewGuid(), "Missing Comic", 1, null, "Draft", 1, now));

        await AssertPostgresViolation(
            PostgresErrorCodes.UniqueViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 1, "duplicate-order", "Ready", 100, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.UniqueViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, "comic-management/chapter/page-1", "Ready", 100, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, " ", "Ready", 100, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, "page-2", "Uploading", 100, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(
                context,
                Guid.NewGuid(),
                chapterId,
                2,
                "page-2",
                "Ready",
                100,
                100,
                "not-a-content-fingerprint"));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, "page-2", "Ready", 0, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, "page-2", "Ready", 100, 0));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, "page-2", "Pending", 100, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.ForeignKeyViolation,
            () => InsertPage(context, Guid.NewGuid(), Guid.NewGuid(), 1, "missing-chapter", "Ready", 100, 100));

        await AssertPostgresViolation(
            PostgresErrorCodes.RestrictViolation,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM catalog.chapters WHERE id = {chapterId};"));
        await AssertPostgresViolation(
            PostgresErrorCodes.RestrictViolation,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM catalog.comics WHERE id = {comicId};"));
    }

    [Fact]
    public async Task Pending_upload_intent_is_concurrency_safe_and_ready_transition_is_idempotent()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using (var setupScope = serviceProvider.CreateAsyncScope())
        {
            var setupContext = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await setupContext.Database.MigrateAsync();
            var now = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
            setupContext.Comics.Add(Comic.Create(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                "concurrent-page-upload",
                "Concurrent Page Upload",
                "",
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                now));
            setupContext.Chapters.Add(Chapter.Create(
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                "Chapter 1",
                1,
                null,
                null,
                now));
            await setupContext.SaveChangesAsync();
        }

        var pageId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var chapterId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        const string publicId = "comic-management/chapter-pages/concurrent/page";

        static async Task<ChapterPage> RegisterPending(
            IServiceProvider serviceProvider,
            Guid pageId,
            Guid chapterId,
            string publicId)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IChapterPageUploadStore>();
            return await store.GetOrCreatePendingAsync(
                ChapterPage.CreatePending(
                    pageId,
                    chapterId,
                    1,
                    publicId,
                    UploadFingerprint,
                    UploadCreatedAt));
        }

        var registrations = await Task.WhenAll(
            RegisterPending(serviceProvider, pageId, chapterId, publicId),
            RegisterPending(serviceProvider, pageId, chapterId, publicId));

        Assert.All(registrations, page => Assert.Equal(ChapterPageAssetStatuses.Pending, page.AssetStatus));

        await using var verifyScope = serviceProvider.CreateAsyncScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verifyContext.ChapterPages.CountAsync(page => page.Id == pageId));

        var uploadStore = verifyScope.ServiceProvider.GetRequiredService<IChapterPageUploadStore>();
        var asset = new ChapterPageAsset(publicId, 1200, 1800);
        var first = await uploadStore.MarkReadyAsync(pageId, asset);
        var retry = await uploadStore.MarkReadyAsync(pageId, asset);

        Assert.True(first.IsValid);
        Assert.True(retry.IsValid);
    }

    [Fact]
    public async Task Provider_success_followed_by_database_failure_recovers_the_same_asset_and_intent()
    {
        var chapterId = Guid.NewGuid();
        await using var serviceProvider = await CreateChapterAsync(chapterId, "upload-recovery");
        var pageId = Guid.NewGuid();
        var assetStore = new RecoveringAssetStore();
        var command = CreateUploadCommand(chapterId, pageId, [1, 2, 3]);

        await using (var firstScope = serviceProvider.CreateAsyncScope())
        {
            var persistentStore = firstScope.ServiceProvider.GetRequiredService<IChapterPageUploadStore>();
            var handler = new UploadChapterPageHandler(
                assetStore,
                new FailBeforeReadyStore(persistentStore),
                new StubClock(UploadCreatedAt));

            await Assert.ThrowsAsync<TimeoutException>(() => handler.HandleAsync(command));
        }

        await using (var mismatchedScope = serviceProvider.CreateAsyncScope())
        {
            var handler = new UploadChapterPageHandler(
                assetStore,
                mismatchedScope.ServiceProvider.GetRequiredService<IChapterPageUploadStore>(),
                new StubClock(UploadCreatedAt.AddMinutes(1)));

            await Assert.ThrowsAsync<ChapterPageUploadConflictException>(() =>
                handler.HandleAsync(CreateUploadCommand(chapterId, pageId, [9, 9, 9])));
        }

        await using (var retryScope = serviceProvider.CreateAsyncScope())
        {
            var handler = new UploadChapterPageHandler(
                assetStore,
                retryScope.ServiceProvider.GetRequiredService<IChapterPageUploadStore>(),
                new StubClock(UploadCreatedAt.AddMinutes(2)));

            var recovered = await handler.HandleAsync(CreateUploadCommand(chapterId, pageId, [1, 2, 3]));

            Assert.True(recovered.IsValid);
            Assert.Equal(1, assetStore.CreatedCount);
            Assert.Equal(1, assetStore.RecoveredCount);
        }
    }

    [Fact]
    public async Task Concurrent_full_upload_handlers_converge_on_one_ready_page_and_one_provider_asset()
    {
        var chapterId = Guid.NewGuid();
        await using var serviceProvider = await CreateChapterAsync(chapterId, "concurrent-full-upload");
        var pageId = Guid.NewGuid();
        var assetStore = new RecoveringAssetStore(expectedConcurrentUploads: 2);

        async Task<ChapterPage> UploadAsync()
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var handler = new UploadChapterPageHandler(
                assetStore,
                scope.ServiceProvider.GetRequiredService<IChapterPageUploadStore>(),
                new StubClock(UploadCreatedAt));
            return await handler.HandleAsync(CreateUploadCommand(chapterId, pageId, [4, 5, 6]));
        }

        var results = await Task.WhenAll(UploadAsync(), UploadAsync());

        Assert.All(results, page => Assert.True(page.IsValid));
        Assert.Equal(1, assetStore.CreatedCount);
        Assert.Equal(1, assetStore.RecoveredCount);

        await using var verifyScope = serviceProvider.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.ChapterPages.CountAsync(page => page.Id == pageId));
    }

    private async Task<ServiceProvider> CreateChapterAsync(Guid chapterId, string slug)
    {
        var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();
        var comicId = Guid.NewGuid();
        context.Comics.Add(Comic.Create(
            comicId,
            slug,
            slug,
            string.Empty,
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            UploadCreatedAt));
        context.Chapters.Add(Chapter.Create(
            chapterId,
            comicId,
            "Chapter 1",
            1,
            null,
            null,
            UploadCreatedAt));
        await context.SaveChangesAsync();
        return serviceProvider;
    }

    private static UploadChapterPageCommand CreateUploadCommand(
        Guid chapterId,
        Guid pageId,
        byte[] content) => new(
            chapterId,
            pageId,
            1,
            "page.webp",
            "image/webp",
            new MemoryStream(content));

    private sealed class FailBeforeReadyStore(IChapterPageUploadStore inner) : IChapterPageUploadStore
    {
        public Task<ChapterPage> GetOrCreatePendingAsync(
            ChapterPage pendingPage,
            CancellationToken cancellationToken = default) =>
            inner.GetOrCreatePendingAsync(pendingPage, cancellationToken);

        public Task<ChapterPage> MarkReadyAsync(
            Guid pageId,
            ChapterPageAsset asset,
            CancellationToken cancellationToken = default) =>
            throw new TimeoutException("Simulated database failure after provider success.");
    }

    private sealed class RecoveringAssetStore(int expectedConcurrentUploads = 1) : IChapterPageAssetStore
    {
        private readonly HashSet<string> _assets = new(StringComparer.Ordinal);
        private readonly object _sync = new();
        private readonly TaskCompletionSource _uploadGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _uploadArrivals;

        public int CreatedCount { get; private set; }

        public int RecoveredCount { get; private set; }

        public void ValidateUpload(ChapterPageAssetUpload upload)
        {
        }

        public string GetPublicId(Guid chapterId, Guid pageId) =>
            $"comic-management/chapter-pages/{chapterId:N}/{pageId:N}";

        public async Task<ChapterPageAsset> UploadAsync(
            ChapterPageAssetUpload upload,
            CancellationToken cancellationToken = default)
        {
            var publicId = GetPublicId(upload.ChapterId, upload.PageId);
            if (expectedConcurrentUploads > 1)
            {
                if (Interlocked.Increment(ref _uploadArrivals) == expectedConcurrentUploads)
                {
                    _uploadGate.TrySetResult();
                }

                await _uploadGate.Task.WaitAsync(cancellationToken);
            }

            lock (_sync)
            {
                if (_assets.Add(publicId))
                {
                    CreatedCount++;
                }
                else
                {
                    RecoveredCount++;
                }
            }

            return new ChapterPageAsset(publicId, 1200, 1800);
        }

        public Task DeleteAsync(string publicId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static Task<int> InsertChapter(
        AppDbContext context,
        Guid id,
        Guid comicId,
        string label,
        int sortOrder,
        long? priceOverride,
        string publicationStatus,
        long version,
        DateTimeOffset now) => context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.chapters
                (id, comic_id, chapter_label, sort_order, price_override_coins, vip_override,
                 publication_status, version, created_at, updated_at)
            VALUES
                ({id}, {comicId}, {label}, {sortOrder}, {priceOverride}, NULL,
                 {publicationStatus}, {version}, {now}, {now});
            """);

    private static Task<int> InsertPage(
        AppDbContext context,
        Guid id,
        Guid chapterId,
        int pageOrder,
        string publicId,
        string assetStatus,
        int width,
        int height,
        string uploadFingerprint = UploadFingerprint) => context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.chapter_pages
                (id, chapter_id, page_order, cloudinary_public_id, asset_status,
                 upload_fingerprint, upload_created_at, width, height)
            VALUES
                ({id}, {chapterId}, {pageOrder}, {publicId}, {assetStatus},
                 {uploadFingerprint}, {UploadCreatedAt}, {width}, {height});
            """);

    private static async Task AssertPostgresViolation(string sqlState, Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(operation);
        Assert.Equal(sqlState, exception.SqlState);
    }
}
