using ComicManagement.Catalog.Administration;
using ComicManagement.Catalog.Media;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class A01ChapterPageAdministrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("a01_page_admin_tests").WithUsername("postgres").WithPassword("postgres").Build();
    private readonly FakeAssets _assets = new();
    private readonly LockGate _gate = new();
    private readonly CapturingLogger _logger = new();
    private ServiceProvider _services = null!;
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _comic = Guid.NewGuid();
    private readonly Guid _chapter = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IChapterPageAssetStore>(_assets);
        services.AddSingleton<IAdminChapterPageCriticalSectionObserver>(_gate);
        services.AddPersistence(_postgres.GetConnectionString());
        services.AddSingleton<ILogger<AdminChapterPageService>>(_logger);
        _services = services.BuildServiceProvider(validateScopes: true);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO identity.users (id, display_name, email, role, status, version, created_at, updated_at)
            VALUES ({{_actor}}, 'Admin', 'a01-pages@test.local', 'Admin', 'Active', 1, now(), now());
            INSERT INTO catalog.comics (id, slug, title, description, series_status, publication_status,
                cover_asset_id, default_price_coins, default_vip_eligible, version, created_at, updated_at)
            VALUES ({{_comic}}, 'a01-pages', 'Pages', '', 'Ongoing', 'Draft', NULL, 0, false, 1, now(), now());
            INSERT INTO catalog.chapters (id, comic_id, chapter_label, sort_order, publication_status,
                version, created_at, updated_at)
            VALUES ({{_chapter}}, {{_comic}}, '1', 1, 'Draft', 1, now(), now());
            """);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Second_batch_appends_and_audits_in_the_same_committed_workflow()
    {
        var first = await Append(1, "one.png", [1, 2, 3]);
        var second = await Append(first.Manifest!.Version, "two.png", [4, 5, 6]);

        Assert.Equal(AdminPageMutationOutcome.Succeeded, second.Outcome);
        Assert.Equal([1, 2], second.Manifest!.Pages.Select(x => x.PageOrder));
        Assert.Empty(_assets.Deleted);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.TargetType == "ChapterPage"));
    }

    [Fact]
    public async Task Stale_version_compensates_provider_asset_and_creates_no_page()
    {
        var result = await Append(99, "stale.png", [1]);

        Assert.Equal(AdminPageMutationOutcome.Conflict, result.Outcome);
        Assert.Single(_assets.Deleted);
        await using var scope = _services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChapterPages.ToListAsync());
    }

    [Fact]
    public async Task Contending_requests_really_wait_and_only_one_expected_version_wins()
    {
        _gate.HoldNext();
        var first = Append(1, "first.png", [1]);
        await _gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Append(1, "second.png", [2]);
        await Task.Delay(250);
        Assert.False(second.IsCompleted);

        _gate.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, x => x.Outcome == AdminPageMutationOutcome.Succeeded);
        Assert.Single(results, x => x.Outcome == AdminPageMutationOutcome.Conflict);
        Assert.Single(_assets.Deleted);
    }

    [Fact]
    public async Task Manifest_uses_one_snapshot_for_chapter_version_and_pages()
    {
        var initial = await Append(1, "one.png", [1]);
        _gate.HoldNextRead();
        await using var readScope = _services.CreateAsyncScope();
        var read = readScope.ServiceProvider.GetRequiredService<IAdminChapterPageService>()
            .GetAsync(_chapter, default);
        await _gate.ReadEntered.WaitAsync(TimeSpan.FromSeconds(10));

        var appended = await Append(initial.Manifest!.Version, "two.png", [2]);
        Assert.Equal(AdminPageMutationOutcome.Succeeded, appended.Outcome);
        _gate.ReleaseRead();

        var snapshot = await read;
        Assert.NotNull(snapshot);
        Assert.Equal(initial.Manifest.Version, snapshot.Version);
        Assert.Single(snapshot.Pages);
    }

    [Fact]
    public async Task Reorder_replace_remove_use_versions_and_cannot_remove_last_published_page()
    {
        var initial = await AppendMany(1, ("one.png", new byte[] { 1 }), ("two.png", new byte[] { 2 }));
        await using var scope = _services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminChapterPageService>();
        var reversed = initial.Manifest!.Pages.Reverse().Select(x => x.Id).ToArray();
        var reordered = await service.ReorderAsync(_chapter, initial.Manifest.Version, reversed,
            _actor, "reorder", default);
        Assert.Equal(reversed, reordered.Manifest!.Pages.Select(x => x.Id));

        await using var replacement = new MemoryStream([9]);
        var replaced = await service.ReplaceAsync(_chapter, reversed[0], reordered.Manifest.Version,
            new("replacement.png", "image/png", replacement), _actor, "replace", default);
        Assert.Equal(AdminPageMutationOutcome.Succeeded, replaced.Outcome);
        Assert.Contains(reversed[0].ToString("N"), _assets.Deleted.Single());

        var removeId = replaced.Manifest!.Pages[1].Id;
        var removed = await service.RemoveAsync(_chapter, removeId, replaced.Manifest.Version,
            _actor, "remove", default);
        Assert.Single(removed.Manifest!.Pages);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.chapters SET publication_status = 'Published', published_at = clock_timestamp() WHERE id = {_chapter}");
        db.ChangeTracker.Clear();

        var rejected = await service.RemoveAsync(_chapter, removed.Manifest.Pages[0].Id,
            removed.Manifest.Version, _actor, "reject-last", default);
        Assert.Equal(AdminPageMutationOutcome.PublicationPreconditionFailed, rejected.Outcome);
    }

    [Fact]
    public async Task Partial_provider_failure_compensates_already_uploaded_assets()
    {
        _assets.FailUploadNumber = 2;
        var result = await AppendMany(1, ("one.png", new byte[] { 1 }), ("two.png", new byte[] { 2 }));
        Assert.Equal(AdminPageMutationOutcome.ProviderFailed, result.Outcome);
        Assert.Single(_assets.Deleted);
    }

    [Fact]
    public async Task Cancellation_on_file_n_compensates_prior_uploads_and_preserves_cancellation()
    {
        _assets.FailUploadNumber = 2;
        _assets.UploadFailure = new OperationCanceledException("cancelled by caller");
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AppendMany(1, ("one.png", new byte[] { 1 }), ("two.png", new byte[] { 2 })));
        Assert.Single(_assets.Deleted);
        Assert.Contains(_chapter.ToString("N"), _assets.Deleted[0]);
    }

    [Fact]
    public async Task Unexpected_failure_on_file_n_compensates_prior_uploads_and_preserves_error()
    {
        _assets.FailUploadNumber = 2;
        _assets.UploadFailure = new InvalidOperationException("unexpected provider failure");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppendMany(1, ("one.png", new byte[] { 1 }), ("two.png", new byte[] { 2 })));
        Assert.Equal("unexpected provider failure", error.Message);
        Assert.Single(_assets.Deleted);
    }

    [Fact]
    public async Task Database_failure_after_uploads_compensates_every_uncommitted_asset()
    {
        await using var setupScope = _services.CreateAsyncScope();
        var setup = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION catalog.a01_reject_page_insert() RETURNS trigger
            LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'forced A01 metadata failure'; END $$;
            CREATE TRIGGER a01_reject_page_insert BEFORE INSERT ON catalog.chapter_pages
            FOR EACH ROW EXECUTE FUNCTION catalog.a01_reject_page_insert();
            """);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                AppendMany(1, ("one.png", new byte[] { 1 }), ("two.png", new byte[] { 2 })));
            Assert.Equal(2, _assets.Deleted.Count);
            await using var verifyScope = _services.CreateAsyncScope();
            Assert.Empty(await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>()
                .ChapterPages.ToListAsync());
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS a01_reject_page_insert ON catalog.chapter_pages;
                DROP FUNCTION IF EXISTS catalog.a01_reject_page_insert();
                """);
        }
    }

    [Fact]
    public async Task Compensation_failure_log_contains_provider_key_and_context()
    {
        _assets.FailUploadNumber = 2;
        _assets.FailDeletes = true;
        var result = await AppendMany(1, ("one.png", new byte[] { 1 }), ("two.png", new byte[] { 2 }));
        Assert.Equal(AdminPageMutationOutcome.ProviderFailed, result.Outcome);
        var entry = Assert.Single(_logger.Messages, message => message.Contains("Cloudinary compensation failed"));
        Assert.Contains(_assets.Deleted[0], entry);
        Assert.Contains(_chapter.ToString(), entry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_commit_provider_cleanup_failure_does_not_rollback_remove()
    {
        var appended = await Append(1, "one.png", [1]);
        _assets.FailDeletes = true;
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAdminChapterPageService>().RemoveAsync(
            _chapter, appended.Manifest!.Pages[0].Id, appended.Manifest.Version,
            _actor, "cleanup-failure", default);

        Assert.Equal(AdminPageMutationOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Manifest!.Pages);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChapterPages.ToListAsync());
    }

    private async Task<AdminPageMutationResult> Append(long version, string name, byte[] bytes)
    {
        await using var scope = _services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminChapterPageService>();
        await using var stream = new MemoryStream(bytes);
        return await service.AppendAsync(_chapter, version,
            [new AdminPageUpload(name, "image/png", stream)], _actor, Guid.NewGuid().ToString("N"), default);
    }

    private async Task<AdminPageMutationResult> AppendMany(long version, params (string Name, byte[] Bytes)[] values)
    {
        await using var scope = _services.CreateAsyncScope();
        var streams = values.Select(x => new MemoryStream(x.Bytes)).ToArray();
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IAdminChapterPageService>().AppendAsync(
                _chapter, version, values.Select((x, index) =>
                    new AdminPageUpload(x.Name, "image/png", streams[index])).ToArray(),
                _actor, Guid.NewGuid().ToString("N"), default);
        }
        finally
        {
            foreach (var stream in streams) await stream.DisposeAsync();
        }
    }

    private sealed class FakeAssets : IChapterPageAssetStore
    {
        public List<string> Deleted { get; } = [];
        public int? FailUploadNumber { get; set; }
        public bool FailDeletes { get; set; }
        public Exception? UploadFailure { get; set; }
        private int _uploadCount;
        public void ValidateUpload(ChapterPageAssetUpload upload) { }
        public string GetPublicId(Guid chapterId, Guid pageId) => $"test/{chapterId:N}/{pageId:N}";
        public Task<ChapterPageAsset> UploadAsync(ChapterPageAssetUpload upload, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _uploadCount) == FailUploadNumber)
                throw UploadFailure ?? new ChapterPageAssetException("PROVIDER_FAILED", "Provider failed.");
            return Task.FromResult(new ChapterPageAsset(GetPublicId(upload.ChapterId, upload.PageId), 800, 1200));
        }
        public Task DeleteAsync(string publicId, CancellationToken cancellationToken = default)
        {
            lock (Deleted) Deleted.Add(publicId);
            if (FailDeletes) throw new ChapterPageAssetException("CLEANUP_FAILED", "Cleanup failed.");
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger<AdminChapterPageService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class LockGate : IAdminChapterPageCriticalSectionObserver
    {
        private TaskCompletionSource _entered = NewSource();
        private TaskCompletionSource _release = NewSource();
        private int _hold;
        private TaskCompletionSource _readEntered = NewSource();
        private TaskCompletionSource _readRelease = NewSource();
        private int _holdRead;
        public Task Entered => _entered.Task;
        public Task ReadEntered => _readEntered.Task;
        public void HoldNext() { _entered = NewSource(); _release = NewSource(); Volatile.Write(ref _hold, 1); }
        public void Release() => _release.TrySetResult();
        public void HoldNextRead() { _readEntered = NewSource(); _readRelease = NewSource(); Volatile.Write(ref _holdRead, 1); }
        public void ReleaseRead() => _readRelease.TrySetResult();
        public async Task AfterChapterLockedAsync(Guid chapterId, CancellationToken token)
        {
            if (Interlocked.Exchange(ref _hold, 0) != 1) return;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(token);
        }
        public async Task AfterManifestChapterReadAsync(Guid chapterId, CancellationToken token)
        {
            if (Interlocked.Exchange(ref _holdRead, 0) != 1) return;
            _readEntered.TrySetResult();
            await _readRelease.Task.WaitAsync(token);
        }
        private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
