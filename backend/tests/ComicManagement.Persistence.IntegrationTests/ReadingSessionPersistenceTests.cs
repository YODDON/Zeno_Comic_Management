using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Persistence;
using ComicManagement.Reading.Events;
using ComicManagement.Reading.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class ReadingSessionPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 10, 14, 2, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_reading_session_tests")
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
    public async Task Migration_creates_reading_session_guards_and_operational_indexes()
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var constraints = await context.Database.SqlQueryRaw<string>(
                "SELECT conname AS \"Value\" FROM pg_constraint " +
                "WHERE connamespace = 'reading'::regnamespace")
            .ToListAsync();
        var indexes = await context.Database.SqlQueryRaw<string>(
                "SELECT indexname AS \"Value\" FROM pg_indexes " +
                "WHERE schemaname = 'reading'")
            .ToListAsync();

        Assert.Contains("ck_reading_sessions_expiry", constraints);
        Assert.Contains("ck_reading_sessions_sequence", constraints);
        Assert.Contains("ck_reading_sessions_qualification", constraints);
        Assert.Contains("ck_reading_progress_offset", constraints);
        Assert.Contains("ix_reading_sessions_user_started_id", indexes);
        Assert.Contains("ix_reading_sessions_chapter_qualified_at", indexes);
        Assert.Contains("ix_reading_progress_user_last_read_at", indexes);
    }

    [Fact]
    public async Task Create_requires_current_public_ready_access_and_uses_server_policy()
    {
        var seed = await SeedPublicChapterAsync(pageOrders: [10, 20]);
        ReadingSessionCommandResult result;
        await using (var scope = _services.CreateAsyncScope())
        {
            result = await scope.ServiceProvider
                .GetRequiredService<IReadingSessionStore>()
                .CreateAsync(
                    Guid.NewGuid(),
                    seed.UserId,
                    seed.ChapterId,
                    ReadingQualificationPolicy.R02V1,
                    StartedAt);
        }

        Assert.Equal(ReadingSessionCommandOutcome.Succeeded, result.Outcome);
        Assert.Equal(StartedAt.AddMinutes(30), result.State!.ExpiresAt);
        Assert.Equal(15_000, result.State.MinimumActiveDurationMs);
        Assert.Equal(2, result.State.ManifestPageCount);

        await using (var mutationScope = _services.CreateAsyncScope())
        {
            await mutationScope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Chapters
                .Where(chapter => chapter.Id == seed.ChapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PublicationStatus,
                    ChapterPublicationStatuses.Hidden));
        }

        await using var deniedScope = _services.CreateAsyncScope();
        var denied = await deniedScope.ServiceProvider
            .GetRequiredService<IReadingSessionStore>()
            .CreateAsync(
                Guid.NewGuid(),
                seed.UserId,
                seed.ChapterId,
                ReadingQualificationPolicy.R02V1,
                StartedAt);
        Assert.Equal(ReadingSessionCommandOutcome.NotFound, denied.Outcome);
    }

    [Fact]
    public async Task Concurrent_same_sequence_and_payload_credits_active_time_once()
    {
        var seed = await SeedPublicChapterAsync(pageOrders: [1]);
        var session = await CreateSessionAsync(seed);
        await UpdateAsync(session.SessionId, seed.UserId, 1, 0.95m, 1, 'a', StartedAt.AddSeconds(1));

        var first = UpdateInNewScopeAsync(
            session.SessionId, seed.UserId, 1, 0.95m, 2, 'b', StartedAt.AddSeconds(31));
        var second = UpdateInNewScopeAsync(
            session.SessionId, seed.UserId, 1, 0.95m, 2, 'b', StartedAt.AddSeconds(31));
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
            Assert.Equal(ReadingSessionCommandOutcome.Succeeded, result.Outcome));

        await using var verificationScope = _services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .ReadingSessions
            .AsNoTracking()
            .SingleAsync(item => item.Id == session.SessionId);
        Assert.Equal(2, persisted.LastAcceptedSequence);
        Assert.Equal(15_000, persisted.ActiveDurationMs);
    }

    [Fact]
    public async Task Manifest_change_remaps_ready_ordinal_without_reducing_accepted_max()
    {
        var seed = await SeedPublicChapterAsync(pageOrders: [10, 20]);
        var session = await CreateSessionAsync(seed);
        var first = await UpdateAsync(
            session.SessionId, seed.UserId, 20, 0.8m, 1, 'a', StartedAt.AddSeconds(1));
        Assert.Equal(9_000, first.State!.AcceptedMaxProgressBasisPoints);
        var oldRevision = first.State.ManifestRevision;

        await using (var mutationScope = _services.CreateAsyncScope())
        {
            await mutationScope.ServiceProvider.GetRequiredService<AppDbContext>()
                .ChapterPages
                .Where(page => page.ChapterId == seed.ChapterId && page.PageOrder == 20)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(page => page.AssetStatus, ChapterPageAssetStatuses.Pending)
                    .SetProperty(page => page.Width, 0)
                    .SetProperty(page => page.Height, 0));
        }

        var remapped = await UpdateAsync(
            session.SessionId, seed.UserId, 20, 1m, 2, 'b', StartedAt.AddSeconds(10));

        Assert.Equal(ReadingSessionCommandOutcome.Succeeded, remapped.Outcome);
        Assert.Equal(9_000, remapped.State!.AcceptedMaxProgressBasisPoints);
        Assert.NotEqual(oldRevision, remapped.State.ManifestRevision);
        Assert.Equal(1, remapped.State.ManifestPageCount);
        Assert.Equal(0, remapped.State.ActiveDurationMs);
        Assert.Equal(10, remapped.State.CurrentPageOrder);
        Assert.Equal(1m, remapped.State.CurrentPageOffsetRatio);

        await using var verificationScope = _services.CreateAsyncScope();
        var progress = await verificationScope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .ReadingProgress
            .AsNoTracking()
            .SingleAsync(item =>
                item.UserId == seed.UserId && item.ComicId == seed.ComicId);
        Assert.Equal(10, progress.LastPageOrder);
        Assert.Equal(1m, progress.LastPageOffsetRatio);
    }

    [Fact]
    public async Task Qualification_remaps_removed_position_and_emits_current_manifest_revision()
    {
        var seed = await SeedPublicChapterAsync(pageOrders: [10, 20]);
        var session = await CreateSessionAsync(seed);
        await UpdateAsync(
            session.SessionId, seed.UserId, 20, 0.8m, 1, 'a', StartedAt.AddSeconds(1));
        await UpdateAsync(
            session.SessionId, seed.UserId, 20, 0.8m, 2, 'b', StartedAt.AddSeconds(16));

        await using (var mutationScope = _services.CreateAsyncScope())
        {
            await mutationScope.ServiceProvider.GetRequiredService<AppDbContext>()
                .ChapterPages
                .Where(page => page.ChapterId == seed.ChapterId && page.PageOrder == 20)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(page => page.AssetStatus, ChapterPageAssetStatuses.Pending)
                    .SetProperty(page => page.Width, 0)
                    .SetProperty(page => page.Height, 0));
        }

        var result = await QualifyInNewScopeAsync(
            session.SessionId,
            seed.UserId,
            StartedAt.AddSeconds(17));

        Assert.Equal(ReadingSessionCommandOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.State!.QualifiedAt);
        Assert.Equal(10, result.State.CurrentPageOrder);
        Assert.Equal(1m, result.State.CurrentPageOffsetRatio);
        Assert.NotEqual(session.ManifestRevision, result.State.ManifestRevision);
        Assert.Equal(1, result.State.ManifestPageCount);

        await using var verificationScope = _services.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var progress = await context.ReadingProgress
            .AsNoTracking()
            .SingleAsync(item =>
                item.UserId == seed.UserId && item.ComicId == seed.ComicId);
        Assert.Equal(10, progress.LastPageOrder);
        Assert.Equal(1m, progress.LastPageOffsetRatio);
        var payload = await context.OutboxMessages
            .Where(message => message.EventType == ReadingEventTypes.ChapterReadQualified)
            .Select(message => message.Payload)
            .SingleAsync();
        Assert.Contains(result.State.ManifestRevision, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Heartbeat_does_not_wait_for_catalog_content_row_locks()
    {
        var seed = await SeedPublicChapterAsync(pageOrders: [1, 2]);
        var session = await CreateSessionAsync(seed);
        await UpdateAsync(
            session.SessionId, seed.UserId, 1, 0.5m, 1, 'a', StartedAt.AddSeconds(1));

        await using var lockScope = _services.CreateAsyncScope();
        var lockContext = lockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var lockTransaction = await lockContext.Database.BeginTransactionAsync();
        await lockContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM catalog.comics WHERE id = {seed.ComicId} FOR UPDATE");
        await lockContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM catalog.chapters WHERE id = {seed.ChapterId} FOR UPDATE");
        await lockContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM catalog.chapter_pages WHERE chapter_id = {seed.ChapterId} FOR UPDATE");

        var heartbeat = await UpdateInNewScopeAsync(
                session.SessionId,
                seed.UserId,
                2,
                0.5m,
                2,
                'b',
                StartedAt.AddSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReadingSessionCommandOutcome.Succeeded, heartbeat.Outcome);
        await lockTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Concurrent_qualification_writes_exactly_one_outbox_message()
    {
        var seed = await SeedPublicChapterAsync(pageOrders: [1]);
        var session = await CreateSessionAsync(seed);
        await UpdateAsync(session.SessionId, seed.UserId, 1, 0.95m, 1, 'a', StartedAt.AddSeconds(1));
        await UpdateAsync(session.SessionId, seed.UserId, 1, 0.95m, 2, 'b', StartedAt.AddSeconds(16));

        var results = await Task.WhenAll(
            QualifyInNewScopeAsync(session.SessionId, seed.UserId, StartedAt.AddSeconds(17)),
            QualifyInNewScopeAsync(session.SessionId, seed.UserId, StartedAt.AddSeconds(17)));

        Assert.All(results, result =>
        {
            Assert.Equal(ReadingSessionCommandOutcome.Succeeded, result.Outcome);
            Assert.NotNull(result.State!.QualifiedAt);
        });

        await using var verificationScope = _services.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.OutboxMessages.CountAsync(message =>
            message.EventType == ReadingEventTypes.ChapterReadQualified));
        var payload = await context.OutboxMessages
            .Where(message => message.EventType == ReadingEventTypes.ChapterReadQualified)
            .Select(message => message.Payload)
            .SingleAsync();
        Assert.Contains(session.SessionId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(seed.UserId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(seed.ComicId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(seed.ChapterId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r02-v1", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qualification_rechecks_access_and_writes_no_outbox_when_revoked()
    {
        var seed = await SeedPublicChapterAsync(pageOrders: [1]);
        var session = await CreateSessionAsync(seed);
        await UpdateAsync(session.SessionId, seed.UserId, 1, 0.95m, 1, 'a', StartedAt.AddSeconds(1));
        await UpdateAsync(session.SessionId, seed.UserId, 1, 0.95m, 2, 'b', StartedAt.AddSeconds(16));

        await using (var mutationScope = _services.CreateAsyncScope())
        {
            await mutationScope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Chapters
                .Where(chapter => chapter.Id == seed.ChapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PublicationStatus,
                    ChapterPublicationStatuses.Hidden));
        }

        var result = await QualifyInNewScopeAsync(
            session.SessionId,
            seed.UserId,
            StartedAt.AddSeconds(17));

        Assert.Equal(ReadingSessionCommandOutcome.NotFound, result.Outcome);
        await using var verificationScope = _services.CreateAsyncScope();
        Assert.Empty(await verificationScope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .OutboxMessages
            .Where(message => message.EventType == ReadingEventTypes.ChapterReadQualified)
            .ToListAsync());
    }

    private async Task<SeededChapter> SeedPublicChapterAsync(int[] pageOrders)
    {
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            "Reading User",
            null,
            null,
            StartedAt));
        var comic = Comic.Create(
            comicId,
            $"reading-{comicId:N}",
            "Reading Sessions",
            string.Empty,
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            StartedAt);
        var chapter = Chapter.Create(
            chapterId,
            comicId,
            "Chapter 1",
            1,
            null,
            null,
            StartedAt);
        foreach (var pageOrder in pageOrders)
        {
            var page = ChapterPage.CreatePending(
                Guid.NewGuid(),
                chapterId,
                pageOrder,
                $"reading/{chapterId:N}/{pageOrder}",
                $"sha256:{pageOrder:x64}",
                StartedAt);
            page.MarkReady(1200, 1800);
            context.ChapterPages.Add(page);
        }

        chapter.Publish(true, 1, StartedAt.AddMinutes(1));
        comic.Publish(true, 1, StartedAt.AddMinutes(2));
        context.Comics.Add(comic);
        context.Chapters.Add(chapter);
        await context.SaveChangesAsync();
        return new SeededChapter(userId, comicId, chapterId);
    }

    private async Task<ReadingSessionState> CreateSessionAsync(SeededChapter seed)
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IReadingSessionStore>()
            .CreateAsync(
                Guid.NewGuid(),
                seed.UserId,
                seed.ChapterId,
                ReadingQualificationPolicy.R02V1,
                StartedAt);
        Assert.Equal(ReadingSessionCommandOutcome.Succeeded, result.Outcome);
        return result.State!;
    }

    private Task<ReadingSessionCommandResult> UpdateAsync(
        Guid sessionId,
        Guid userId,
        int pageOrder,
        decimal offset,
        long sequence,
        char fingerprint,
        DateTimeOffset receivedAt) => UpdateInNewScopeAsync(
        sessionId,
        userId,
        pageOrder,
        offset,
        sequence,
        fingerprint,
        receivedAt);

    private async Task<ReadingSessionCommandResult> UpdateInNewScopeAsync(
        Guid sessionId,
        Guid userId,
        int pageOrder,
        decimal offset,
        long sequence,
        char fingerprint,
        DateTimeOffset receivedAt)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IReadingSessionStore>()
            .UpdateProgressAsync(
                sessionId,
                userId,
                pageOrder,
                offset,
                sequence,
                new string(fingerprint, 64),
                receivedAt);
    }

    private async Task<ReadingSessionCommandResult> QualifyInNewScopeAsync(
        Guid sessionId,
        Guid userId,
        DateTimeOffset qualifiedAt)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IReadingSessionStore>()
            .QualifyAsync(sessionId, userId, qualifiedAt);
    }

    private sealed record SeededChapter(Guid UserId, Guid ComicId, Guid ChapterId);
}
