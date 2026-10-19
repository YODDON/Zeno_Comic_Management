using ComicManagement.Identity.Users;
using ComicManagement.Missions.Administration;
using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Processing;
using ComicManagement.Missions.Progress;
using ComicManagement.Missions.Queries;
using ComicManagement.Missions.Receipts;
using ComicManagement.Missions.Rewards;
using ComicManagement.Reading.Events;
using ComicManagement.Shared.Time;
using ComicManagement.Wallet.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class M02MissionPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_m02_tests")
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
            .AddSingleton<IClock>(new FixedClock(BaseTime.AddDays(10)))
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
    public async Task Historical_selection_uses_half_open_boundaries_and_never_current_fallback()
    {
        var userId = await SeedUserAsync();
        var aStart = BaseTime.AddHours(1);
        var boundary = BaseTime.AddHours(2);
        var (revisionA, revisionB) = await SeedTwoRevisionTimelineAsync(
            MissionActionTypes.ChapterReadQualified,
            aStart,
            boundary,
            boundary,
            reward: 10);

        await AddReceiptAsync(userId, aStart.AddTicks(-10), Guid.NewGuid());
        await AddReceiptAsync(userId, aStart.AddMinutes(30), Guid.NewGuid());
        await AddReceiptAsync(userId, boundary, Guid.NewGuid());
        await DrainAsync();

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var progress = await context.MissionProgress.AsNoTracking()
            .OrderBy(item => item.MissionRevisionId)
            .ToArrayAsync();
        Assert.Equal(2, progress.Length);
        Assert.Contains(progress, item => item.MissionRevisionId == revisionA.Id);
        Assert.Contains(progress, item => item.MissionRevisionId == revisionB.Id);
        Assert.All(progress, item => Assert.Equal(1, item.ProgressCount));
        Assert.Equal(3, await context.ActionReceipts.CountAsync(item =>
            item.ProcessingStatus == ActionReceiptStatuses.Processed));
        Assert.Equal(2, await context.MissionRewardGrants.CountAsync());
    }

    [Fact]
    public async Task Deactivation_gap_does_not_fallback_to_current_revision()
    {
        var userId = await SeedUserAsync();
        var aStart = BaseTime.AddHours(1);
        var aEnd = BaseTime.AddHours(2);
        var bStart = BaseTime.AddHours(3);
        var (_, revisionB) = await SeedTwoRevisionTimelineAsync(
            MissionActionTypes.ComicFollowed,
            aStart,
            aEnd,
            bStart,
            reward: 10);
        await AddReceiptAsync(
            userId,
            BaseTime.AddHours(2).AddMinutes(30),
            Guid.NewGuid(),
            ReadingEventTypes.ComicFollowed);

        await DrainAsync();

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.MissionProgress.AsNoTracking().ToArrayAsync());
        Assert.False(await context.MissionProgress.AnyAsync(item =>
            item.MissionRevisionId == revisionB.Id));
        Assert.Equal(
            ActionReceiptStatuses.Processed,
            await context.ActionReceipts.Select(item => item.ProcessingStatus).SingleAsync());
    }

    [Fact]
    public async Task Concurrent_mission_completions_serialize_on_wallet_and_never_exceed_daily_cap()
    {
        var userId = await SeedUserAsync();
        await using (var walletScope = Services.CreateAsyncScope())
        {
            var walletContext = walletScope.ServiceProvider.GetRequiredService<AppDbContext>();
            walletContext.Wallets.Add(WalletAccount.Create(userId, BaseTime));
            await walletContext.SaveChangesAsync();
        }
        await SeedSingleActiveMissionAsync(
            MissionActionTypes.ChapterReadQualified, BaseTime, 1, 200, "cap-one");
        await SeedSingleActiveMissionAsync(
            MissionActionTypes.ComicFollowed, BaseTime, 1, 200, "cap-two");
        var readReceiptId = await AddReceiptAsync(
            userId, BaseTime.AddHours(1), Guid.NewGuid());
        var followReceiptId = await AddReceiptAsync(
            userId,
            BaseTime.AddHours(1),
            Guid.NewGuid(),
            ReadingEventTypes.ComicFollowed);

        var observer = new BlockingReceiptObserver();
        await using var firstProvider = CreateProvider(observer);
        await using var secondProvider = CreateProvider(observer);
        await using var firstScope = firstProvider.CreateAsyncScope();
        await using var secondScope = secondProvider.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<MissionReceiptProcessor>()
            .ProcessAsync(readReceiptId);
        await observer.Entered;
        var second = secondScope.ServiceProvider.GetRequiredService<MissionReceiptProcessor>()
            .ProcessAsync(followReceiptId);
        await Task.Delay(150);
        Assert.False(second.IsCompleted);
        observer.Release();
        Assert.All(
            await Task.WhenAll(first, second),
            outcome => Assert.Equal(MissionReceiptProcessingOutcome.Processed, outcome));

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var grants = await context.MissionRewardGrants.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, grants.Length);
        Assert.Single(grants, item => item.Outcome == MissionRewardOutcomes.Granted);
        Assert.Single(grants, item => item.Outcome == MissionRewardOutcomes.DeniedDailyCap);
        Assert.Equal(200, await context.MissionDailyCounters
            .Select(item => item.GrantedTotal).SingleAsync());
        Assert.Equal(200, await context.Wallets.Select(item => item.BonusAvailable).SingleAsync());
        Assert.Equal(200, await context.WalletEntries
            .Where(item => item.CoinType == WalletCoinTypes.Bonus)
            .SumAsync(item => item.DeltaCoins));
        Assert.Single(await context.WalletOperations.Where(item =>
            item.OperationType == WalletOperationTypes.MissionReward &&
            item.SourceType == WalletOperationSourceTypes.RewardGrant).ToArrayAsync());
        var outbox = await context.OutboxMessages.AsNoTracking()
            .Where(item => item.EventType == MissionEventTypes.RewardGranted)
            .SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<MissionRewardGranted>(
            outbox.Payload,
            new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
        var granted = Assert.Single(grants, item =>
            item.Outcome == MissionRewardOutcomes.Granted);
        Assert.NotNull(payload);
        Assert.Equal(granted.Id, payload.RewardGrantId);
        Assert.Equal(userId, payload.UserId);
        Assert.Equal(granted.BonusCoins, payload.BonusCoins);
        Assert.Equal(granted.LocalDate, payload.ActionLocalDate);

        var queryStore = scope.ServiceProvider.GetRequiredService<IMissionQueryStore>();
        var firstPage = await queryStore.ListRewardsAsync(userId, null, 1);
        var firstReward = Assert.Single(firstPage);
        var secondPage = await queryStore.ListRewardsAsync(
            userId,
            new MissionRewardCursor(firstReward.DecidedAt, firstReward.RewardGrantId),
            1);
        Assert.Single(secondPage);
        Assert.NotEqual(firstReward.RewardGrantId, secondPage[0].RewardGrantId);
    }

    [Fact]
    public async Task Duplicate_subject_in_same_period_advances_progress_once()
    {
        var userId = await SeedUserAsync();
        await SeedSingleActiveMissionAsync(
            MissionActionTypes.ChapterReadQualified, BaseTime, 2, 25, "subject-dedup");
        var chapterId = Guid.NewGuid();
        await AddReceiptAsync(userId, BaseTime.AddHours(1), chapterId);
        await AddReceiptAsync(userId, BaseTime.AddHours(2), chapterId);

        await DrainAsync();

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.MissionProgress.Select(item => item.ProgressCount).SingleAsync());
        Assert.Single(await context.ProgressActions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.MissionRewardGrants.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Processing_failure_rolls_back_every_side_effect_and_pending_receipt_retries()
    {
        var userId = await SeedUserAsync();
        await SeedSingleActiveMissionAsync(
            MissionActionTypes.ChapterReadQualified, BaseTime, 1, 40, "retry-after-rollback");
        var receiptId = await AddReceiptAsync(
            userId, BaseTime.AddHours(1), Guid.NewGuid());

        await using (var failingProvider = CreateProvider(new ThrowingReceiptObserver()))
        await using (var failingScope = failingProvider.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InjectedMissionProcessingException>(() =>
                failingScope.ServiceProvider.GetRequiredService<MissionReceiptProcessor>()
                    .ProcessAsync(receiptId));
        }

        await using (var assertionScope = Services.CreateAsyncScope())
        {
            var context = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(
                ActionReceiptStatuses.Pending,
                (await context.ActionReceipts.FindAsync(receiptId))!.ProcessingStatus);
            Assert.False(await context.Wallets.AnyAsync(item => item.UserId == userId));
            Assert.Empty(await context.MissionProgress.AsNoTracking().ToArrayAsync());
            Assert.Empty(await context.MissionRewardGrants.AsNoTracking().ToArrayAsync());
            Assert.Empty(await context.WalletOperations.Where(item =>
                item.OperationType == WalletOperationTypes.MissionReward).ToArrayAsync());
            Assert.Empty(await context.OutboxMessages.Where(item =>
                item.EventType == MissionEventTypes.RewardGranted).ToArrayAsync());
        }

        await using (var retryScope = Services.CreateAsyncScope())
        {
            Assert.Equal(
                MissionReceiptProcessingOutcome.Processed,
                await retryScope.ServiceProvider.GetRequiredService<MissionReceiptProcessor>()
                    .ProcessAsync(receiptId));
        }

        await using var completedScope = Services.CreateAsyncScope();
        var completed = completedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            ActionReceiptStatuses.Processed,
            (await completed.ActionReceipts.FindAsync(receiptId))!.ProcessingStatus);
        Assert.Equal(40, await completed.Wallets
            .Where(item => item.UserId == userId)
            .Select(item => item.BonusAvailable)
            .SingleAsync());
        Assert.Single(await completed.MissionRewardGrants.Where(item =>
            item.UserId == userId && item.Outcome == MissionRewardOutcomes.Granted)
            .ToArrayAsync());
    }

    [Fact]
    public async Task Activation_history_rejects_mutation_delete_and_overlap()
    {
        await SeedUserAsync();
        await SeedSingleActiveMissionAsync(
            MissionActionTypes.DailyLogin, BaseTime, 1, 10, "immutable-history");

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activation = await context.MissionActivations.SingleAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE missions.mission_activations
            SET deactivated_at = {BaseTime.AddHours(1)}
            WHERE id = {activation.Id};
            """);
        await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE missions.mission_activations
                SET deactivated_at = {BaseTime.AddHours(2)}
                WHERE id = {activation.Id};
                """));
        await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM missions.mission_activations
                WHERE id = {activation.Id};
                """));
        context.ChangeTracker.Clear();

        var missionId = await context.MissionDefinitions.Select(item => item.Id).SingleAsync();
        var revisionId = await context.MissionRevisions.Select(item => item.Id).SingleAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO missions.mission_activations
                    (id, mission_definition_id, mission_revision_id,
                     activation_sequence, activated_at, deactivated_at)
                VALUES
                    ({Guid.NewGuid()}, {missionId}, {revisionId}, 2,
                     {BaseTime.AddMinutes(1)}, NULL);
                """));
    }

    [Fact]
    public async Task Reward_grant_rejects_non_mission_operation_and_rolls_back_financial_rows()
    {
        var userId = await SeedUserAsync();
        var definition = MissionDefinition.Create(
            Guid.NewGuid(), "bad-reward-ledger", "Bad reward ledger", string.Empty,
            MissionActionTypes.ChapterReadQualified, MissionCycleTypes.Daily, BaseTime);
        var revision = MissionRevision.Create(
            Guid.NewGuid(), definition.Id, 1, 1, 30,
            MissionRepeatPolicies.UniqueSubjectPerPeriod, BaseTime, null, "{}");
        var progress = MissionProgress.Create(
            Guid.NewGuid(), userId, revision.Id, "daily:2026-10-18");
        Assert.True(progress.Increment(1, BaseTime.AddHours(1)));
        var rewardId = Guid.NewGuid();
        var operation = WalletOperation.Create(
            Guid.NewGuid(),
            userId,
            WalletOperationTypes.TopUpCredit,
            WalletOperationSourceTypes.PaymentOrder,
            Guid.NewGuid(),
            null,
            BaseTime.AddHours(2));
        var reward = MissionRewardGrant.Create(
            rewardId,
            progress.Id,
            userId,
            30,
            MissionRewardOutcomes.Granted,
            new DateOnly(2026, 10, 18),
            BaseTime.AddHours(2),
            operation.Id);

        await using (var scope = Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.MissionDefinitions.Add(definition);
            context.MissionRevisions.Add(revision);
            await context.SaveChangesAsync();
            context.MissionProgress.Add(progress);
            context.WalletOperations.Add(operation);
            context.WalletEntries.Add(WalletEntry.Create(
                Guid.NewGuid(), operation, WalletCoinTypes.Bonus,
                WalletBuckets.Available, 30, 1, BaseTime.AddHours(2)));
            context.MissionRewardGrants.Add(reward);
            await context.SaveChangesAsync();
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await using var assertionScope = Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await assertionContext.MissionRewardGrants.AnyAsync(item =>
            item.Id == rewardId));
        Assert.False(await assertionContext.WalletOperations.AnyAsync(item =>
            item.Id == operation.Id));
        Assert.False(await assertionContext.WalletEntries.AnyAsync(item =>
            item.OperationId == operation.Id));
    }

    [Fact]
    public async Task Concurrent_activations_serialize_and_allocate_one_next_sequence()
    {
        var actorId = await SeedUserAsync();
        var definition = MissionDefinition.Create(
            Guid.NewGuid(), "activation-race", "Activation race", string.Empty,
            MissionActionTypes.DailyLogin, MissionCycleTypes.Daily, BaseTime.AddDays(-1));
        var revisionA = MissionRevision.Create(
            Guid.NewGuid(), definition.Id, 1, 1, 10,
            MissionRepeatPolicies.OncePerVietnamDate, BaseTime, null, "{}");
        var revisionB = MissionRevision.Create(
            Guid.NewGuid(), definition.Id, 2, 1, 10,
            MissionRepeatPolicies.OncePerVietnamDate, BaseTime, null, "{}");
        var revisionC = MissionRevision.Create(
            Guid.NewGuid(), definition.Id, 3, 1, 10,
            MissionRepeatPolicies.OncePerVietnamDate, BaseTime, null, "{}");
        await using (var seedScope = Services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.MissionDefinitions.Add(definition);
            context.MissionRevisions.AddRange(revisionA, revisionB, revisionC);
            await context.SaveChangesAsync();
            definition.Activate(revisionA, 1);
            context.MissionActivations.Add(MissionActivation.Create(
                Guid.NewGuid(), definition.Id, revisionA.Id, 1, BaseTime));
            await context.SaveChangesAsync();
        }

        var observer = new BlockingActivationObserver();
        await using var firstProvider = CreateProvider(observer);
        await using var secondProvider = CreateProvider(observer);
        await using var firstScope = firstProvider.CreateAsyncScope();
        await using var secondScope = secondProvider.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IMissionAdministrationStore>()
            .ChangeActivationAsync(new ChangeMissionActivationStoreRequest(
                definition.Id, revisionB.Id, actorId, 2, "race-first"));
        await observer.Entered;
        var second = secondScope.ServiceProvider.GetRequiredService<IMissionAdministrationStore>()
            .ChangeActivationAsync(new ChangeMissionActivationStoreRequest(
                definition.Id, revisionC.Id, actorId, 2, "race-second"));
        await Task.Delay(150);
        Assert.False(second.IsCompleted);
        observer.Release();

        var results = await Task.WhenAll(first, second);
        Assert.Single(results, item => item.Outcome == MissionAdministrationOutcome.Succeeded);
        Assert.Single(results, item =>
            item.Outcome == MissionAdministrationOutcome.ConcurrencyConflict);

        await using var assertionScope = Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var history = await assertionContext.MissionActivations.AsNoTracking()
            .OrderBy(item => item.ActivationSequence).ToArrayAsync();
        Assert.Equal([1L, 2L], history.Select(item => item.ActivationSequence));
        Assert.NotNull(history[0].DeactivatedAt);
        Assert.Null(history[1].DeactivatedAt);
    }

    private async Task<Guid> SeedUserAsync()
    {
        var userId = Guid.NewGuid();
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId, "M02 User", null, null, BaseTime.AddDays(-1)));
        await context.SaveChangesAsync();
        return userId;
    }

    private async Task<(MissionRevision A, MissionRevision B)> SeedTwoRevisionTimelineAsync(
        string actionType,
        DateTimeOffset aStart,
        DateTimeOffset aEnd,
        DateTimeOffset bStart,
        long reward)
    {
        var definition = MissionDefinition.Create(
            Guid.NewGuid(), $"timeline-{Guid.NewGuid():N}", "Timeline", string.Empty,
            actionType, MissionCycleTypes.Fixed, BaseTime.AddDays(-1));
        var policy = actionType == MissionActionTypes.DailyLogin
            ? MissionRepeatPolicies.OncePerVietnamDate
            : MissionRepeatPolicies.UniqueSubjectPerPeriod;
        var revisionA = MissionRevision.Create(
            Guid.NewGuid(), definition.Id, 1, 1, reward, policy,
            aStart, aEnd, "{}");
        var revisionB = MissionRevision.Create(
            Guid.NewGuid(), definition.Id, 2, 1, reward, policy,
            bStart, BaseTime.AddDays(1), "{}");
        var activationA = MissionActivation.Create(
            Guid.NewGuid(), definition.Id, revisionA.Id, 1, aStart);
        activationA.Close(aEnd);
        var activationB = MissionActivation.Create(
            Guid.NewGuid(), definition.Id, revisionB.Id, 2, bStart);

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.MissionDefinitions.Add(definition);
        context.MissionRevisions.AddRange(revisionA, revisionB);
        await context.SaveChangesAsync();
        definition.Activate(revisionB, 1);
        context.MissionActivations.AddRange(activationA, activationB);
        await context.SaveChangesAsync();
        return (revisionA, revisionB);
    }

    private async Task SeedSingleActiveMissionAsync(
        string actionType,
        DateTimeOffset activatedAt,
        int target,
        long reward,
        string code)
    {
        var definition = MissionDefinition.Create(
            Guid.NewGuid(), code, code, string.Empty, actionType,
            MissionCycleTypes.Daily, BaseTime.AddDays(-1));
        var repeatPolicy = actionType == MissionActionTypes.DailyLogin
            ? MissionRepeatPolicies.OncePerVietnamDate
            : MissionRepeatPolicies.UniqueSubjectPerPeriod;
        var revision = MissionRevision.Create(
            Guid.NewGuid(), definition.Id, 1, target, reward, repeatPolicy,
            activatedAt, null, "{}");
        var activation = MissionActivation.Create(
            Guid.NewGuid(), definition.Id, revision.Id, 1, activatedAt);
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.MissionDefinitions.Add(definition);
        context.MissionRevisions.Add(revision);
        await context.SaveChangesAsync();
        definition.Activate(revision, 1);
        context.MissionActivations.Add(activation);
        await context.SaveChangesAsync();
    }

    private async Task<Guid> AddReceiptAsync(
        Guid userId,
        DateTimeOffset occurredAt,
        Guid subjectId,
        string sourceEventType = ReadingEventTypes.ChapterReadQualified)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var receiptId = Guid.NewGuid();
        context.ActionReceipts.Add(ActionReceipt.Create(
            receiptId, Guid.NewGuid(), sourceEventType, userId, subjectId,
            new string('a', 64), occurredAt, BaseTime.AddDays(5)));
        await context.SaveChangesAsync();
        return receiptId;
    }

    private async Task DrainAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var scope = Services.CreateAsyncScope();
            var outcome = await scope.ServiceProvider
                .GetRequiredService<MissionReceiptProcessor>()
                .ProcessNextAsync();
            if (outcome == MissionReceiptProcessingOutcome.NoWork)
            {
                return;
            }
        }
        throw new InvalidOperationException("Mission receipt drain did not converge.");
    }

    private ServiceProvider CreateProvider(IMissionActivationCriticalSectionObserver observer) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IMissionActivationCriticalSectionObserver>(observer)
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);

    private ServiceProvider CreateProvider(IMissionReceiptCriticalSectionObserver observer) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IMissionReceiptCriticalSectionObserver>(observer)
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);

    private sealed class BlockingActivationObserver : IMissionActivationCriticalSectionObserver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        internal Task Entered => _entered.Task;

        public async ValueTask DefinitionLockedAsync(
            Guid missionDefinitionId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        internal void Release() => _release.TrySetResult();
    }

    private sealed class BlockingReceiptObserver : IMissionReceiptCriticalSectionObserver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        internal Task Entered => _entered.Task;

        public async ValueTask WalletLockedAsync(
            Guid userId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        internal void Release() => _release.TrySetResult();
    }

    private sealed class ThrowingReceiptObserver : IMissionReceiptCriticalSectionObserver
    {
        public ValueTask WalletLockedAsync(
            Guid userId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new InjectedMissionProcessingException());
    }

    private sealed class InjectedMissionProcessingException : Exception;

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
