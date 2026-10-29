using System.Text.Json;
using ComicManagement.Billing.Payments;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Membership.Subscriptions;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Events;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Shared.Messaging;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class ChapterUnlockPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_chapter_unlock_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = BuildServices();
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
    public async Task Unlock_is_bonus_first_paid_fifo_atomic_and_replay_safe()
    {
        var seed = await SeedAsync(_services, 80, 30, 100);

        var first = await UnlockAsync(_services, seed, "unlock-one");
        var replay = await UnlockAsync(_services, seed, "unlock-one");
        var differentKey = await UnlockAsync(_services, seed, "unlock-two");

        Assert.Equal(UnlockChapterOutcome.Succeeded, first.Outcome);
        Assert.Equal(30, first.State!.BonusCoins);
        Assert.Equal(50, first.State.PaidCoins);
        Assert.False(first.State.AlreadyUnlocked);
        Assert.False(replay.State!.AlreadyUnlocked);
        Assert.True(differentKey.State!.AlreadyUnlocked);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(value => value.UserId == seed.UserId);
        var lot = await context.PaidCoinLots.AsNoTracking()
            .SingleAsync(value => value.UserId == seed.UserId);
        var unlock = await context.ChapterUnlocks.AsNoTracking().SingleAsync();
        var operation = await context.WalletOperations.AsNoTracking()
            .SingleAsync(value => value.Id == unlock.WalletOperationId);

        Assert.Equal(0, wallet.BonusAvailable);
        Assert.Equal(50, wallet.PaidAvailable);
        Assert.Equal(50, lot.SpentCoins);
        Assert.Equal(WalletOperationTypes.ChapterUnlock, operation.OperationType);
        Assert.Equal(WalletOperationSourceTypes.ReadingUnlock, operation.SourceType);
        Assert.Equal(unlock.Id, operation.SourceId);
        Assert.Equal(2, await context.WalletEntries.CountAsync(value =>
            value.OperationId == operation.Id));
        Assert.Equal(50, await context.WalletAllocations
            .Where(value => value.OperationId == operation.Id)
            .SumAsync(value => value.AmountCoins));
        var outbox = Assert.Single(await context.OutboxMessages.AsNoTracking()
            .Where(value => value.EventType == ReadingEventTypes.ChapterUnlocked)
            .ToArrayAsync());
        var payload = JsonSerializer.Deserialize<ChapterUnlocked>(
            outbox.Payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.Equal(unlock.Id, payload.UnlockId);
        Assert.Equal(unlock.UserId, payload.UserId);
        Assert.Equal(unlock.ChapterId, payload.ChapterId);
        Assert.Equal(unlock.QuoteId, payload.QuoteId);
        Assert.Equal(operation.Id, payload.WalletOperationId);
        Assert.Equal(80, payload.PricePaidCoins);
        Assert.Equal(30, payload.BonusSpentCoins);
        Assert.Equal(50, payload.PaidSpentCoins);
        var accessProjection = await scope.ServiceProvider
            .GetRequiredService<IChapterAccessProjectionStore>()
            .GetManyAsync([seed.ChapterId], seed.UserId);
        Assert.Equal(
            ChapterAccessTypes.CoinUnlocked,
            ChapterAccessPolicy.Resolve(
                accessProjection[seed.ChapterId],
                isAuthenticated: true).AccessType);
        await FinancialReconciliationAssertions.AssertWalletAndLotsAsync(context, seed.UserId);

        context.ChapterUnlocks.Remove(unlock);
        var appendOnly = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync());
        Assert.Equal("Chapter unlocks are append-only.", appendOnly.Message);
        context.ChangeTracker.Clear();

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var databaseAppendOnly = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, $"DELETE FROM reading.chapter_unlocks WHERE id = '{unlock.Id:D}'"));
        Assert.Equal("55000", databaseAppendOnly.SqlState);
    }

    [Fact]
    public async Task Same_key_with_another_quote_conflicts_without_another_debit()
    {
        var seed = await SeedAsync(_services, 80, 0, 200);
        var secondQuoteId = await AddQuoteAsync(_services, seed, 80);

        Assert.Equal(
            UnlockChapterOutcome.Succeeded,
            (await UnlockAsync(_services, seed, "same-key")).Outcome);
        var conflict = await UnlockAsync(
            _services,
            seed with { QuoteId = secondQuoteId },
            "same-key");

        Assert.Equal(UnlockChapterOutcome.IdempotencyConflict, conflict.Outcome);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await context.ChapterUnlocks.ToArrayAsync());
        Assert.Equal(120, (await context.Wallets.SingleAsync()).PaidAvailable);
    }

    [Fact]
    public async Task Exact_replay_ignores_later_wallet_drift_and_missing_request_quote()
    {
        var seed = await SeedAsync(_services, 80, 0, 200);
        Assert.Equal(
            UnlockChapterOutcome.Succeeded,
            (await UnlockAsync(_services, seed, "stable-replay")).Outcome);

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Wallets
                .Where(wallet => wallet.UserId == seed.UserId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    wallet => wallet.PaidAvailable,
                    wallet => wallet.PaidAvailable + 1));
        }

        var replay = await UnlockAsync(_services, seed, "stable-replay");
        var conflict = await UnlockAsync(
            _services,
            seed with { QuoteId = Guid.NewGuid() },
            "stable-replay");

        Assert.Equal(UnlockChapterOutcome.Succeeded, replay.Outcome);
        Assert.False(replay.State!.AlreadyUnlocked);
        Assert.Equal(UnlockChapterOutcome.IdempotencyConflict, conflict.Outcome);
        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await verification.ChapterUnlocks.ToArrayAsync());
        Assert.Single(await verification.WalletOperations
            .Where(operation => operation.OperationType == WalletOperationTypes.ChapterUnlock)
            .ToArrayAsync());
        Assert.Single(await verification.OutboxMessages
            .Where(message => message.EventType == ReadingEventTypes.ChapterUnlocked)
            .ToArrayAsync());
    }

    [Fact]
    public async Task Quote_lock_wait_crossing_expiry_fails_closed()
    {
        var observer = new SignalingObserver();
        await using var services = BuildServices(collection =>
        {
            collection.RemoveAll<IChapterUnlockCriticalSectionObserver>();
            collection.AddSingleton<IChapterUnlockCriticalSectionObserver>(observer);
        });
        var seed = await SeedAsync(
            services,
            80,
            0,
            100,
            quoteCreatedAt: DateTimeOffset.UtcNow - ChapterUnlockQuote.TimeToLive +
                TimeSpan.FromMilliseconds(900));

        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            $"SELECT 1 FROM reading.chapter_unlock_quotes WHERE id = '{seed.QuoteId:D}' FOR UPDATE");

        var unlockTask = UnlockAsync(services, seed, "quote-expiry-race");
        await observer.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1_100);
        await blockerTransaction.CommitAsync();

        Assert.Equal(UnlockChapterOutcome.QuoteExpired, (await unlockTask).Outcome);
        await AssertNoUnlockSideEffectsAsync(services, seed.UserId);
    }

    [Fact]
    public async Task Vip_lock_wait_crossing_expiry_reprices_and_fails_closed()
    {
        var observer = new SignalingObserver();
        await using var services = BuildServices(collection =>
        {
            collection.RemoveAll<IChapterUnlockCriticalSectionObserver>();
            collection.AddSingleton<IChapterUnlockCriticalSectionObserver>(observer);
        });
        var vipExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(900);
        var seed = await SeedAsync(
            services,
            100,
            0,
            100,
            vipExpiresAt: vipExpiresAt,
            vipDiscountPercent: ChapterUnlockPricingPolicy.VipDiscountPercent);

        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));",
            blocker,
            blockerTransaction))
        {
            lockCommand.Parameters.AddWithValue("lock_key", $"vip-subscription:{seed.UserId:N}");
            await lockCommand.ExecuteNonQueryAsync();
        }

        var unlockTask = UnlockAsync(services, seed, "vip-expiry-race");
        await observer.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1_100);
        await blockerTransaction.CommitAsync();

        Assert.Equal(UnlockChapterOutcome.PriceChanged, (await unlockTask).Outcome);
        await AssertNoUnlockSideEffectsAsync(services, seed.UserId);
    }

    [Fact]
    public async Task Insufficient_coins_produces_no_financial_or_entitlement_side_effect()
    {
        var seed = await SeedAsync(_services, 80, 10, 20);

        var result = await UnlockAsync(_services, seed, "insufficient");

        Assert.Equal(UnlockChapterOutcome.InsufficientCoins, result.Outcome);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.ChapterUnlocks.ToArrayAsync());
        Assert.False(await context.WalletOperations.AnyAsync(value =>
            value.OperationType == WalletOperationTypes.ChapterUnlock));
        Assert.False(await context.OutboxMessages.AnyAsync(value =>
            value.EventType == ReadingEventTypes.ChapterUnlocked));
        var wallet = await context.Wallets.SingleAsync();
        Assert.Equal(10, wallet.BonusAvailable);
        Assert.Equal(20, wallet.PaidAvailable);
    }

    [Fact]
    public async Task Expired_or_changed_quote_and_hidden_chapter_do_not_mutate_wallet()
    {
        var expired = await SeedAsync(
            _services,
            80,
            0,
            100,
            quoteCreatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.Equal(
            UnlockChapterOutcome.QuoteExpired,
            (await UnlockAsync(_services, expired, "expired")).Outcome);

        var changed = await SeedAsync(_services, 80, 0, 100);
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Chapters
                .Where(value => value.Id == changed.ChapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PriceOverrideCoins,
                    (long?)81));
        }
        Assert.Equal(
            UnlockChapterOutcome.PriceChanged,
            (await UnlockAsync(_services, changed, "changed")).Outcome);

        var hidden = await SeedAsync(_services, 80, 0, 100);
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Chapters
                .Where(value => value.Id == hidden.ChapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PublicationStatus,
                    ChapterPublicationStatuses.Hidden));
        }
        Assert.Equal(
            UnlockChapterOutcome.NotFound,
            (await UnlockAsync(_services, hidden, "hidden")).Outcome);

        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await verification.ChapterUnlocks.ToArrayAsync());
        Assert.False(await verification.WalletOperations.AnyAsync(value =>
            value.OperationType == WalletOperationTypes.ChapterUnlock));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_unlocks_really_contend_and_only_one_debits(
        bool sameIdempotencyKey)
    {
        var observer = new BlockingObserver();
        await using var services = BuildServices(collection =>
        {
            collection.RemoveAll<IChapterUnlockCriticalSectionObserver>();
            collection.AddSingleton<IChapterUnlockCriticalSectionObserver>(observer);
        });
        var seed = await SeedAsync(services, 80, 20, 100);

        var firstTask = UnlockAsync(services, seed, "race-a");
        await observer.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = UnlockAsync(
            services,
            seed,
            sameIdempotencyKey ? "race-a" : "race-b");
        await Task.Delay(300);
        Assert.False(secondTask.IsCompleted);

        observer.Release();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(UnlockChapterOutcome.Succeeded, result.Outcome));
        if (sameIdempotencyKey)
        {
            Assert.All(results, result => Assert.False(result.State!.AlreadyUnlocked));
        }
        else
        {
            Assert.Single(results, result => result.State is { AlreadyUnlocked: false });
            Assert.Single(results, result => result.State is { AlreadyUnlocked: true });
        }
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await context.ChapterUnlocks.ToArrayAsync());
        Assert.Single(await context.WalletOperations
            .Where(value => value.OperationType == WalletOperationTypes.ChapterUnlock)
            .ToArrayAsync());
        Assert.Equal(40, (await context.Wallets.SingleAsync()).PaidAvailable);
        await FinancialReconciliationAssertions.AssertWalletAndLotsAsync(context, seed.UserId);
    }

    [Fact]
    public async Task Downstream_outbox_failure_rolls_back_debit_ledger_and_entitlement()
    {
        await using var services = BuildServices(collection =>
        {
            collection.RemoveAll<IOutboxWriter>();
            collection.AddScoped<IOutboxWriter, ThrowingOutboxWriter>();
        });
        var seed = await SeedAsync(services, 80, 20, 100);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UnlockAsync(services, seed, "rollback"));

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking().SingleAsync();
        var lot = await context.PaidCoinLots.AsNoTracking().SingleAsync();
        Assert.Equal(20, wallet.BonusAvailable);
        Assert.Equal(100, wallet.PaidAvailable);
        Assert.Equal(0, lot.SpentCoins);
        Assert.Empty(await context.ChapterUnlocks.ToArrayAsync());
        Assert.False(await context.WalletOperations.AnyAsync(value =>
            value.OperationType == WalletOperationTypes.ChapterUnlock));
        await FinancialReconciliationAssertions.AssertWalletAndLotsAsync(context, seed.UserId);
    }

    [Fact]
    public async Task PostgreSql_rejects_cross_owner_quote_and_invalid_spend_snapshot()
    {
        var first = await SeedAsync(_services, 80, 0, 100);
        var second = await SeedAsync(_services, 80, 0, 100);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondOperation = await context.WalletOperations.AsNoTracking()
            .FirstAsync(value => value.UserId == second.UserId);

        var ownership = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reading.chapter_unlocks
                    (id,user_id,chapter_id,quote_id,wallet_operation_id,
                     price_paid_coins,bonus_spent_coins,paid_spent_coins,
                     wallet_paid_available_after,wallet_bonus_available_after,unlocked_at)
                VALUES ({Guid.NewGuid()},{second.UserId},{second.ChapterId},{first.QuoteId},
                        {secondOperation.Id},{80L},{0L},{80L},{20L},{0L},{DateTimeOffset.UtcNow})
                """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ownership.SqlState);

        var invalidSpend = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reading.chapter_unlocks
                    (id,user_id,chapter_id,quote_id,wallet_operation_id,
                     price_paid_coins,bonus_spent_coins,paid_spent_coins,
                     wallet_paid_available_after,wallet_bonus_available_after,unlocked_at)
                VALUES ({Guid.NewGuid()},{second.UserId},{second.ChapterId},{second.QuoteId},
                        {secondOperation.Id},{80L},{0L},{79L},{20L},{0L},{DateTimeOffset.UtcNow})
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidSpend.SqlState);
    }

    [Theory]
    [InlineData("TopUpCredit")]
    [InlineData("WrongOperationType")]
    [InlineData("WrongSourceType")]
    [InlineData("WrongSourceId")]
    [InlineData("MissingLedgerDebit")]
    [InlineData("ExcessLedgerDebit")]
    [InlineData("PaidAllocationMismatch")]
    public async Task PostgreSql_rejects_entitlement_without_matching_unlock_debit(
        string invalidCase)
    {
        var seed = await SeedAsync(_services, 80, 0, 100);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var paidLotId = await context.PaidCoinLots
            .Where(lot => lot.UserId == seed.UserId)
            .Select(lot => lot.Id)
            .SingleAsync();
        var topUpOperationId = await context.WalletOperations
            .Where(operation =>
                operation.UserId == seed.UserId &&
                operation.OperationType == WalletOperationTypes.TopUpCredit)
            .Select(operation => operation.Id)
            .SingleAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertInvalidUnlockAsync(
                seed,
                paidLotId,
                topUpOperationId,
                invalidCase));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        var accessProjection = await scope.ServiceProvider
            .GetRequiredService<IChapterAccessProjectionStore>()
            .GetManyAsync([seed.ChapterId], seed.UserId);
        Assert.Equal(
            ChapterAccessTypes.Locked,
            ChapterAccessPolicy.Resolve(
                accessProjection[seed.ChapterId],
                isAuthenticated: true).AccessType);
        Assert.Empty(await context.ChapterUnlocks
            .Where(unlock => unlock.UserId == seed.UserId)
            .ToArrayAsync());
    }

    private async Task InsertInvalidUnlockAsync(
        Seed seed,
        Guid paidLotId,
        Guid topUpOperationId,
        string invalidCase)
    {
        var unlockId = Guid.NewGuid();
        var operationId = invalidCase == "TopUpCredit"
            ? topUpOperationId
            : Guid.NewGuid();
        var operationType = invalidCase == "WrongOperationType"
            ? "OtherDebit"
            : WalletOperationTypes.ChapterUnlock;
        var sourceType = invalidCase == "WrongSourceType"
            ? "OtherSource"
            : WalletOperationSourceTypes.ReadingUnlock;
        var sourceId = invalidCase == "WrongSourceId"
            ? Guid.NewGuid()
            : unlockId;
        var usesPaid = invalidCase == "PaidAllocationMismatch";

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (invalidCase != "TopUpCredit")
        {
            await ExecuteAsync(
                connection,
                $"""
                INSERT INTO wallet.wallet_operations
                    (id,user_id,operation_type,source_type,source_id,idempotency_key,created_at)
                VALUES ('{operationId:D}','{seed.UserId:D}','{operationType}',
                        '{sourceType}','{sourceId:D}','invalid-{operationId:N}',clock_timestamp())
                """);

            if (invalidCase == "ExcessLedgerDebit")
            {
                await InsertDebitEntryAsync(connection, operationId, seed.UserId, "Bonus", -81);
            }
            else if (usesPaid)
            {
                await InsertDebitEntryAsync(connection, operationId, seed.UserId, "Paid", -80);
                await ExecuteAsync(
                    connection,
                    $"""
                    INSERT INTO wallet.wallet_allocations
                        (id,operation_id,paid_coin_lot_id,amount_coins,created_at)
                    VALUES ('{Guid.NewGuid():D}','{operationId:D}','{paidLotId:D}',79,clock_timestamp())
                    """);
            }
            else if (invalidCase is not "MissingLedgerDebit")
            {
                await InsertDebitEntryAsync(connection, operationId, seed.UserId, "Bonus", -80);
            }
        }

        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO reading.chapter_unlocks
                (id,user_id,chapter_id,quote_id,wallet_operation_id,
                 price_paid_coins,bonus_spent_coins,paid_spent_coins,
                 wallet_paid_available_after,wallet_bonus_available_after,unlocked_at)
            VALUES ('{unlockId:D}','{seed.UserId:D}','{seed.ChapterId:D}','{seed.QuoteId:D}',
                    '{operationId:D}',80,{(usesPaid ? 0 : 80)},{(usesPaid ? 80 : 0)},
                    20,0,clock_timestamp())
            """);

        await transaction.CommitAsync();
    }

    private static Task InsertDebitEntryAsync(
        NpgsqlConnection connection,
        Guid operationId,
        Guid userId,
        string coinType,
        long delta) =>
        ExecuteAsync(
            connection,
            $"""
            INSERT INTO wallet.wallet_entries
                (id,operation_id,user_id,coin_type,bucket,delta_coins,leg_order,created_at)
            VALUES ('{Guid.NewGuid():D}','{operationId:D}','{userId:D}',
                    '{coinType}','Available',{delta},1,clock_timestamp())
            """);

    private ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString());
        configure?.Invoke(collection);
        return collection.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<UnlockChapterResult> UnlockAsync(
        ServiceProvider services,
        Seed seed,
        string idempotencyKey)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ChapterUnlockHandler>()
            .UnlockAsync(
                seed.UserId,
                seed.ChapterId,
                seed.QuoteId,
                idempotencyKey);
    }

    private static async Task<Seed> SeedAsync(
        ServiceProvider services,
        long price,
        long bonusCoins,
        long paidCoins,
        DateTimeOffset? quoteCreatedAt = null,
        DateTimeOffset? vipExpiresAt = null,
        int vipDiscountPercent = 0)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var wallet = WalletAccount.Create(userId, now);
        var operations = new List<WalletOperation>();
        var entries = new List<WalletEntry>();

        if (bonusCoins > 0)
        {
            var bonusOperation = WalletOperation.Create(
                Guid.NewGuid(), userId, "MissionReward", "RewardGrant",
                Guid.NewGuid(), $"bonus-{Guid.NewGuid():N}", now);
            wallet.CreditBonus(bonusCoins, now);
            operations.Add(bonusOperation);
            entries.Add(WalletEntry.Create(
                Guid.NewGuid(), bonusOperation, WalletCoinTypes.Bonus,
                WalletBuckets.Available, bonusCoins, 1, now));
        }

        var order = PaymentOrder.CreateTopUp(
            orderId,
            userId,
            $"TOPUP-{orderId:N}",
            10_000,
            1_000,
            $"topup-order-{orderId:N}",
            new string('a', PaymentOrder.RequestFingerprintLength),
            now,
            now.AddMinutes(30));
        order.FulfillTopUp(now);
        var paidOperation = WalletOperation.Create(
            Guid.NewGuid(), userId, WalletOperationTypes.TopUpCredit,
            WalletOperationSourceTypes.PaymentOrder, orderId,
            $"topup-credit-{orderId:N}", now);
        var lot = PaidCoinLot.Create(Guid.NewGuid(), userId, orderId, paidCoins, now);
        wallet.CreditPaid(paidCoins, now);
        operations.Add(paidOperation);
        entries.Add(WalletEntry.Create(
            Guid.NewGuid(), paidOperation, WalletCoinTypes.Paid,
            WalletBuckets.Available, paidCoins, 1, now));

        var comic = Comic.Create(
            comicId,
            $"unlock-{comicId:N}",
            "Unlock Test",
            string.Empty,
            ComicSeriesStatuses.Ongoing,
            null,
            price,
            false,
            now);
        var chapter = Chapter.Create(
            chapterId, comicId, "Chapter 1", 1, null, null, now);
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(), chapterId, 1, $"unlock/{chapterId:N}/1",
            $"sha256:{new string('b', 64)}", now);
        page.MarkReady(1200, 1800);
        chapter.Publish(true, 1, now.AddSeconds(1));
        comic.Publish(true, 1, now.AddSeconds(2));
        var quote = ChapterUnlockQuote.Create(
            Guid.NewGuid(), userId, chapterId, price, vipDiscountPercent,
            bonusCoins, paidCoins, quoteCreatedAt ?? DateTimeOffset.UtcNow);

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId, "Unlock User", null, null, now));
        context.Comics.Add(comic);
        context.Chapters.Add(chapter);
        context.ChapterPages.Add(page);
        context.PaymentOrders.Add(order);
        context.Wallets.Add(wallet);
        context.WalletOperations.AddRange(operations);
        context.WalletEntries.AddRange(entries);
        context.PaidCoinLots.Add(lot);
        context.ChapterUnlockQuotes.Add(quote);
        if (vipExpiresAt.HasValue)
        {
            var (subscription, _) = VipSubscription.Create(
                userId,
                vipExpiresAt.Value.AddHours(-720),
                720);
            context.VipSubscriptions.Add(subscription);
        }

        await context.SaveChangesAsync();
        return new Seed(userId, chapterId, quote.Id);
    }

    private static async Task AssertNoUnlockSideEffectsAsync(
        ServiceProvider services,
        Guid userId)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.ChapterUnlocks
            .Where(unlock => unlock.UserId == userId)
            .ToArrayAsync());
        Assert.False(await context.WalletOperations.AnyAsync(operation =>
            operation.UserId == userId &&
            operation.OperationType == WalletOperationTypes.ChapterUnlock));
        Assert.False(await context.OutboxMessages.AnyAsync(message =>
            message.EventType == ReadingEventTypes.ChapterUnlocked));
    }

    private static async Task<Guid> AddQuoteAsync(
        ServiceProvider services,
        Seed seed,
        long price)
    {
        var quote = ChapterUnlockQuote.Create(
            Guid.NewGuid(), seed.UserId, seed.ChapterId,
            price, 0, 0, price, DateTimeOffset.UtcNow);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.ChapterUnlockQuotes.Add(quote);
        await context.SaveChangesAsync();
        return quote.Id;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Seed(Guid UserId, Guid ChapterId, Guid QuoteId);

    private sealed class BlockingObserver : IChapterUnlockCriticalSectionObserver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        public async Task AfterWalletLockedAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SignalingObserver : IChapterUnlockCriticalSectionObserver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        public Task AfterWalletLockedAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingOutboxWriter : IOutboxWriter
    {
        public Guid Enqueue<TPayload>(
            string eventType,
            TPayload payload,
            DateTimeOffset occurredAt)
            where TPayload : notnull =>
            throw new InvalidOperationException("Injected outbox failure.");
    }
}
