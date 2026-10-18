using ComicManagement.Identity.Users;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Events;
using ComicManagement.Billing.Events;
using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Receipts;
using ComicManagement.Reading.Events;
using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Npgsql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class MissionPersistenceTests : IAsyncLifetime
{
    private readonly MutableClock _clock = new(
        new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero));
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_mission_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
    private ServiceProvider? _services;

    private ServiceProvider Services => _services ??
        throw new InvalidOperationException("Test services are unavailable.");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(_clock)
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Qualified_read_receipt_uses_event_time_and_deduplicates_semantic_event()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 10, 18, 16, 59, 59, TimeSpan.Zero);

        await using (var scope = Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Users.Add(User.CreateFromGoogle(
                userId,
                "Mission Reader",
                null,
                null,
                occurredAt.AddDays(-1)));
            var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            var payload = new ChapterReadQualified(
                sessionId,
                userId,
                Guid.NewGuid(),
                chapterId,
                occurredAt,
                "r02-v1",
                9_000,
                15_000,
                new string('a', 64));
            writer.Enqueue(ReadingEventTypes.ChapterReadQualified, payload, occurredAt);
            writer.Enqueue(ReadingEventTypes.ChapterReadQualified, payload, occurredAt);
            await context.SaveChangesAsync();
        }

        await ProcessBatch();

        await using var assertionScope = Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var receipt = await assertionContext.ActionReceipts.AsNoTracking().SingleAsync();
        Assert.Equal(sessionId, receipt.SourceEventId);
        Assert.Equal(ReadingEventTypes.ChapterReadQualified, receipt.SourceEventType);
        Assert.Equal(userId, receipt.UserId);
        Assert.Equal(chapterId, receipt.SubjectId);
        Assert.Equal(occurredAt, receipt.OccurredAt);
        Assert.Equal(new DateOnly(2026, 10, 18), receipt.OccurredLocalDate);
        Assert.Equal(ActionReceiptStatuses.Pending, receipt.ProcessingStatus);
        Assert.Equal(2, await assertionContext.ProcessedMessages.CountAsync());
    }

    [Fact]
    public async Task Mission_constraints_and_revision_ownership_fail_closed()
    {
        var now = _clock.UtcNow;
        var firstDefinition = MissionDefinition.Create(
            Guid.NewGuid(),
            $"mission-{Guid.NewGuid():N}",
            "First mission",
            string.Empty,
            MissionActionTypes.DailyLogin,
            MissionCycleTypes.Daily,
            now);
        var secondDefinition = MissionDefinition.Create(
            Guid.NewGuid(),
            $"mission-{Guid.NewGuid():N}",
            "Second mission",
            string.Empty,
            MissionActionTypes.ComicFollowed,
            MissionCycleTypes.Weekly,
            now);
        var firstRevision = MissionRevision.Create(
            Guid.NewGuid(),
            firstDefinition.Id,
            1,
            1,
            20,
            MissionRepeatPolicies.OncePerVietnamDate,
            now,
            null,
            "{}");

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.MissionDefinitions.AddRange(firstDefinition, secondDefinition);
        context.MissionRevisions.Add(firstRevision);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            firstRevision.GetType().GetProperty(nameof(MissionRevision.TargetCount))!
                .SetValue(firstRevision, 2);
            return context.SaveChangesAsync();
        });
        context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE missions.mission_revisions
                SET target_count = {2}
                WHERE id = {firstRevision.Id};
                """));

        var activeStatus = MissionDefinitionStatuses.Active;
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE missions.mission_definitions
                SET active_revision_id = {firstRevision.Id}, status = {activeStatus}
                WHERE id = {secondDefinition.Id};
                """));

        var wrongPolicyRevision = MissionRevision.Create(
            Guid.NewGuid(),
            firstDefinition.Id,
            2,
            1,
            20,
            MissionRepeatPolicies.UniqueSubjectPerPeriod,
            now.AddMinutes(1),
            null,
            "{}");
        context.MissionRevisions.Add(wrongPolicyRevision);
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE missions.mission_definitions
                SET active_revision_id = {wrongPolicyRevision.Id}, status = {activeStatus}
                WHERE id = {firstDefinition.Id};
                """));

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE missions.mission_definitions
            SET active_revision_id = {firstRevision.Id}, status = {activeStatus}
            WHERE id = {firstDefinition.Id};
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE missions.mission_definitions
                SET action_type = {MissionActionTypes.ComicFollowed}
                WHERE id = {firstDefinition.Id};
                """));

        const string emptyConfiguration = "{}";
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO missions.mission_revisions
                    (id, mission_id, revision_number, target_count,
                     reward_bonus_coins, repeat_policy, effective_from,
                     effective_until, configuration_snapshot)
                VALUES
                    ({Guid.NewGuid()}, {firstDefinition.Id}, {2}, {1}, {301L},
                     {MissionRepeatPolicies.OncePerVietnamDate}, {now}, NULL,
                     {emptyConfiguration}::jsonb);
                """));
    }

    [Fact]
    public async Task Existing_action_events_are_mapped_to_authoritative_subjects()
    {
        var userId = Guid.NewGuid();
        var followedComicId = Guid.NewGuid();
        var unlockedChapterId = Guid.NewGuid();
        var qualifiedChapterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var unlockId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.Zero);

        await using (var scope = Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Users.Add(User.CreateFromGoogle(
                userId, "Mission Actions", null, null, occurredAt.AddDays(-1)));
            var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            writer.Enqueue(
                ReadingEventTypes.ChapterReadQualified,
                new ChapterReadQualified(
                    sessionId, userId, Guid.NewGuid(), qualifiedChapterId, occurredAt,
                    "r02-v1", 9_000, 15_000, new string('b', 64)),
                occurredAt);
            writer.Enqueue(
                ReadingEventTypes.ComicFollowed,
                new ComicFollowed(userId, followedComicId, occurredAt),
                occurredAt);
            writer.Enqueue(
                ReadingEventTypes.ChapterUnlocked,
                new ChapterUnlocked(
                    unlockId, userId, unlockedChapterId, Guid.NewGuid(), Guid.NewGuid(),
                    100, 60, 40, occurredAt),
                occurredAt);
            writer.Enqueue(
                BillingEventTypes.WalletTopUpCompleted,
                new WalletTopUpCompletedEvent(
                    orderId, Guid.NewGuid(), userId, 100_000, 1_000, occurredAt),
                occurredAt);
            await context.SaveChangesAsync();
        }

        await using (var scope = Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
            var result = await processor.ProcessBatchAsync(CancellationToken.None);
            Assert.Equal(4, result.CompletedCount);
            Assert.Equal(0, result.FailedCount);
        }

        await using var assertionScope = Services.CreateAsyncScope();
        var receipts = await assertionScope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .ActionReceipts.AsNoTracking()
            .ToDictionaryAsync(receipt => receipt.SourceEventType);
        Assert.Equal(qualifiedChapterId,
            receipts[ReadingEventTypes.ChapterReadQualified].SubjectId);
        Assert.Equal(sessionId,
            receipts[ReadingEventTypes.ChapterReadQualified].SourceEventId);
        Assert.Equal(followedComicId,
            receipts[ReadingEventTypes.ComicFollowed].SubjectId);
        Assert.Equal(unlockedChapterId,
            receipts[ReadingEventTypes.ChapterUnlocked].SubjectId);
        Assert.Equal(unlockId,
            receipts[ReadingEventTypes.ChapterUnlocked].SourceEventId);
        Assert.Equal(orderId,
            receipts[BillingEventTypes.WalletTopUpCompleted].SubjectId);
        Assert.Equal(orderId,
            receipts[BillingEventTypes.WalletTopUpCompleted].SourceEventId);
    }

    [Fact]
    public async Task Google_sign_in_event_is_persisted_as_daily_login_receipt()
    {
        const string subject = "mission-daily-login-subject";
        await using (var scope = Services.CreateAsyncScope())
        {
            var handler = new GoogleSignInHandler(
                scope.ServiceProvider.GetRequiredService<IGoogleIdentityStore>(),
                _clock);
            var result = await handler.HandleAsync(
                new GoogleUserProfile(subject, "Daily Login User", null, null),
                CancellationToken.None);
            Assert.Equal(GoogleSignInOutcome.Succeeded, result.Outcome);
        }

        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<OutboxBatchProcessor>()
                .ProcessBatchAsync(CancellationToken.None);
        }

        await using var assertionScope = Services.CreateAsyncScope();
        var context = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var receipt = await context.ActionReceipts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.SourceEventType == IdentityEventTypes.UserLoggedIn);
        Assert.Null(receipt.SubjectId);
        Assert.Equal(_clock.UtcNow, receipt.OccurredAt);
        Assert.Equal(new DateOnly(2026, 10, 20), receipt.OccurredLocalDate);
    }

    [Fact]
    public async Task Concurrent_exact_receipt_replay_contends_and_creates_one_row()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var occurredAt = _clock.UtcNow;
        await SeedUser(userId, "Concurrent Mission Receipt");

        await using var blockerConnection = new NpgsqlConnection(_postgres.GetConnectionString());
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(
                         """
                         INSERT INTO missions.action_receipts
                             (id, source_event_id, source_event_type, user_id, subject_id,
                              occurred_at, occurred_local_date, received_at, processing_status)
                         VALUES
                             (@id, @event_id, @event_type, @user_id, NULL,
                              @occurred_at, @local_date, @received_at, 'Pending');
                         """,
                         blockerConnection,
                         blockerTransaction))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("event_id", eventId);
            insert.Parameters.AddWithValue("event_type", IdentityEventTypes.UserLoggedIn);
            insert.Parameters.AddWithValue("user_id", userId);
            insert.Parameters.AddWithValue("occurred_at", occurredAt);
            insert.Parameters.AddWithValue("local_date", new DateOnly(2026, 10, 20));
            insert.Parameters.AddWithValue("received_at", occurredAt);
            await insert.ExecuteNonQueryAsync();
        }

        await using var contenderScope = Services.CreateAsyncScope();
        var contenderContext = contenderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await contenderContext.Database.OpenConnectionAsync();
        var contenderPid = await contenderContext.Database
            .SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync();
        var contender = contenderScope.ServiceProvider
            .GetRequiredService<MissionActionReceiptRecorder>()
            .RecordAsync(
                eventId,
                IdentityEventTypes.UserLoggedIn,
                userId,
                null,
                occurredAt,
                occurredAt,
                CancellationToken.None);

        await WaitForBackendLockAsync(contenderPid);
        Assert.False(contender.IsCompleted);
        await blockerTransaction.CommitAsync();
        await contender;

        Assert.Equal(
            1,
            await contenderContext.ActionReceipts.CountAsync(candidate =>
                candidate.SourceEventId == eventId &&
                candidate.SourceEventType == IdentityEventTypes.UserLoggedIn));
    }

    [Fact]
    public async Task Duplicate_receipt_identity_with_different_payload_fails_closed()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var occurredAt = _clock.UtcNow;
        await SeedUser(userId, "Conflicting Mission Receipt");

        await using var scope = Services.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<MissionActionReceiptRecorder>();
        await recorder.RecordAsync(
            eventId,
            IdentityEventTypes.UserLoggedIn,
            userId,
            null,
            occurredAt,
            occurredAt,
            CancellationToken.None);

        await Assert.ThrowsAsync<MissionActionReceiptConflictException>(() => recorder.RecordAsync(
            eventId,
            IdentityEventTypes.UserLoggedIn,
            userId,
            Guid.NewGuid(),
            occurredAt,
            occurredAt,
            CancellationToken.None));

        var receipt = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .ActionReceipts.AsNoTracking()
            .SingleAsync(candidate => candidate.SourceEventId == eventId);
        Assert.Null(receipt.SubjectId);
    }

    [Fact]
    public async Task Conflicting_outbox_duplicate_is_not_marked_as_processed()
    {
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var loginId = Guid.NewGuid();
        await SeedUser(firstUserId, "First conflicting receipt");
        await SeedUser(secondUserId, "Second conflicting receipt");

        await using (var scope = Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            writer.Enqueue(
                IdentityEventTypes.UserLoggedIn,
                new UserLoggedIn(loginId, firstUserId, _clock.UtcNow),
                _clock.UtcNow);
            writer.Enqueue(
                IdentityEventTypes.UserLoggedIn,
                new UserLoggedIn(loginId, secondUserId, _clock.UtcNow),
                _clock.UtcNow);
            await context.SaveChangesAsync();
        }

        await using (var scope = Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider
                .GetRequiredService<OutboxBatchProcessor>()
                .ProcessBatchAsync(CancellationToken.None);
            Assert.Equal(1, result.CompletedCount);
            Assert.Equal(1, result.FailedCount);
        }

        await using var assertionScope = Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            1,
            await assertionContext.ActionReceipts.CountAsync(candidate =>
                candidate.SourceEventId == loginId));
        Assert.Equal(
            1,
            await assertionContext.ProcessedMessages.CountAsync(processed =>
                processed.Consumer == "Missions.UserLoggedIn.v1"));
    }

    private async Task ProcessBatch()
    {
        await using var scope = Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        var result = await processor.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(0, result.FailedCount);
    }

    private async Task SeedUser(Guid userId, string displayName)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            displayName,
            null,
            null,
            _clock.UtcNow.AddDays(-1)));
        await context.SaveChangesAsync();
    }

    private async Task WaitForBackendLockAsync(int backendPid)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE pid = @pid
                      AND wait_event_type = 'Lock'
                );
                """,
                connection);
            command.Parameters.AddWithValue("pid", backendPid);
            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The duplicate Mission receipt did not contend on its unique row.");
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
