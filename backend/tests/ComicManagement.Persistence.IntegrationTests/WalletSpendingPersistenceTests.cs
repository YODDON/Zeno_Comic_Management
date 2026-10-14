using ComicManagement.Identity.Users;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Spending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class WalletSpendingPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_wallet_spending_tests")
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
    public async Task Spend_is_bonus_first_paid_fifo_atomic_and_idempotent()
    {
        var seed = await SeedWalletAsync(30, (20, Now), (100, Now.AddSeconds(1)));
        var command = Command(seed.UserId, 80, "spend-80");

        var first = await SpendInNewScopeAsync(command, Now.AddMinutes(1));
        var replay = await SpendInNewScopeAsync(command, Now.AddMinutes(2));
        var differentPayload = await SpendInNewScopeAsync(
            command with { AmountCoins = 81 },
            Now.AddMinutes(3));
        var differentKey = await SpendInNewScopeAsync(
            command with { IdempotencyKey = "another-key" },
            Now.AddMinutes(4));

        Assert.Equal(SpendCoinsOutcome.Succeeded, first.Outcome);
        Assert.NotNull(first.Receipt);
        Assert.False(first.Receipt.IsReplay);
        Assert.Equal(30, first.Receipt.BonusCoins);
        Assert.Equal(50, first.Receipt.PaidCoins);
        Assert.Collection(
            first.Receipt.PaidAllocations,
            allocation =>
            {
                Assert.Equal(seed.LotIds[0], allocation.PaidCoinLotId);
                Assert.Equal(20, allocation.AmountCoins);
            },
            allocation =>
            {
                Assert.Equal(seed.LotIds[1], allocation.PaidCoinLotId);
                Assert.Equal(30, allocation.AmountCoins);
            });
        Assert.Equal(SpendCoinsOutcome.Succeeded, replay.Outcome);
        Assert.True(replay.Receipt!.IsReplay);
        Assert.Equal(first.Receipt.OperationId, replay.Receipt.OperationId);
        Assert.Equal(SpendCoinsOutcome.IdempotencyConflict, differentPayload.Outcome);
        Assert.Equal(SpendCoinsOutcome.IdempotencyConflict, differentKey.Outcome);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(value => value.UserId == seed.UserId);
        var lots = await context.PaidCoinLots.AsNoTracking()
            .Where(lot => lot.UserId == seed.UserId)
            .OrderBy(lot => lot.CreatedAt)
            .ThenBy(lot => lot.Id)
            .ToListAsync();
        var operationCount = await context.WalletOperations.CountAsync(operation =>
            operation.UserId == seed.UserId &&
            operation.OperationType == command.OperationType);
        var debitEntries = await context.WalletEntries.AsNoTracking()
            .Where(entry => entry.OperationId == first.Receipt.OperationId)
            .OrderBy(entry => entry.LegOrder)
            .ToListAsync();
        var persistedAllocations = await context.WalletAllocations.AsNoTracking()
            .Where(allocation => allocation.OperationId == first.Receipt.OperationId)
            .ToListAsync();

        Assert.Equal(0, wallet.BonusAvailable);
        Assert.Equal(70, wallet.PaidAvailable);
        Assert.Equal(20, lots[0].SpentCoins);
        Assert.Equal(30, lots[1].SpentCoins);
        Assert.Equal(1, operationCount);
        Assert.Equal([-30L, -50L], debitEntries.Select(entry => entry.DeltaCoins));
        Assert.Equal(50, persistedAllocations.Sum(allocation => allocation.AmountCoins));
        await AssertReconciledAsync(context, seed.UserId);

        context.WalletAllocations.Remove(persistedAllocations[0]);
        var efAppendOnly = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync());
        Assert.Equal("Wallet ledger records are append-only.", efAppendOnly.Message);
        context.ChangeTracker.Clear();

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var databaseAppendOnly = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, $"""
                UPDATE wallet.wallet_allocations
                SET amount_coins = amount_coins + 1
                WHERE id = '{persistedAllocations[0].Id:D}'
                """));
        Assert.Equal("55000", databaseAppendOnly.SqlState);
    }

    [Fact]
    public async Task Insufficient_spend_rolls_back_without_ledger_or_allocation()
    {
        var seed = await SeedWalletAsync(10, (20, Now));
        var result = await SpendInNewScopeAsync(
            Command(seed.UserId, 31, "insufficient"),
            Now.AddMinutes(1));

        Assert.Equal(SpendCoinsOutcome.InsufficientCoins, result.Outcome);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(value => value.UserId == seed.UserId);
        var lot = await context.PaidCoinLots.AsNoTracking()
            .SingleAsync(value => value.Id == seed.LotIds[0]);

        Assert.Equal(10, wallet.BonusAvailable);
        Assert.Equal(20, wallet.PaidAvailable);
        Assert.Equal(0, lot.SpentCoins);
        Assert.Empty(await context.WalletAllocations.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(
            await context.WalletOperations.AsNoTracking().ToListAsync(),
            operation => operation.OperationType == "ChapterUnlock");
        await AssertReconciledAsync(context, seed.UserId);
    }

    [Fact]
    public async Task Concurrent_spends_serialize_on_wallet_and_never_overdraw()
    {
        var seed = await SeedWalletAsync(0, (100, Now));
        await using var blocker = await HoldWalletLockAsync(seed.UserId);
        var startGate = new ConcurrentStartGate(participantCount: 2);

        var firstTask = SpendInNewScopeAsync(
            Command(seed.UserId, 80, "race-a"),
            Now.AddMinutes(1),
            startGate);
        var secondTask = SpendInNewScopeAsync(
            Command(seed.UserId, 80, "race-b"),
            Now.AddMinutes(1),
            startGate);

        await ThrowIfCompletedUnexpectedlyAsync(firstTask, secondTask);
        await WaitForWalletLockWaitersAsync(blocker, expectedWaiters: 1);
        Assert.False(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);
        await blocker.Transaction.CommitAsync();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => result.Outcome == SpendCoinsOutcome.Succeeded);
        Assert.Single(results, result => result.Outcome == SpendCoinsOutcome.InsufficientCoins);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(value => value.UserId == seed.UserId);
        var lot = await context.PaidCoinLots.AsNoTracking()
            .SingleAsync(value => value.Id == seed.LotIds[0]);

        Assert.Equal(20, wallet.PaidAvailable);
        Assert.Equal(80, lot.SpentCoins);
        Assert.Equal(80, await context.WalletAllocations.SumAsync(value => value.AmountCoins));
        Assert.Equal(1, await context.WalletOperations.CountAsync(operation =>
            operation.OperationType == "ChapterUnlock"));
        await AssertReconciledAsync(context, seed.UserId);
    }

    [Fact]
    public async Task Concurrent_same_request_debits_once_and_replays_once()
    {
        var seed = await SeedWalletAsync(10, (100, Now));
        var command = Command(seed.UserId, 60, "same-race");
        await using var blocker = await HoldWalletLockAsync(seed.UserId);
        var startGate = new ConcurrentStartGate(participantCount: 2);

        var firstTask = SpendInNewScopeAsync(command, Now.AddMinutes(1), startGate);
        var secondTask = SpendInNewScopeAsync(command, Now.AddMinutes(1), startGate);

        await ThrowIfCompletedUnexpectedlyAsync(firstTask, secondTask);
        await WaitForWalletLockWaitersAsync(blocker, expectedWaiters: 1);
        Assert.False(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);
        await blocker.Transaction.CommitAsync();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(SpendCoinsOutcome.Succeeded, result.Outcome));
        Assert.Single(results, result => result.Receipt is { IsReplay: false });
        Assert.Single(results, result => result.Receipt is { IsReplay: true });
        Assert.Single(results.Select(result => result.Receipt!.OperationId).Distinct());

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(value => value.UserId == seed.UserId);

        Assert.Equal(0, wallet.BonusAvailable);
        Assert.Equal(50, wallet.PaidAvailable);
        Assert.Equal(1, await context.WalletOperations.CountAsync(operation =>
            operation.OperationType == "ChapterUnlock"));
        Assert.Equal(50, await context.WalletAllocations.SumAsync(value => value.AmountCoins));
        await AssertReconciledAsync(context, seed.UserId);
    }

    [Fact]
    public async Task Failure_after_debit_before_commit_rolls_back_all_wallet_changes()
    {
        var seed = await SeedWalletAsync(10, (100, Now));
        var command = Command(seed.UserId, 60, "fault-before-commit");

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var result = await scope.ServiceProvider.GetRequiredService<WalletSpendEngine>()
                .ApplyAsync(command, Now.AddMinutes(1), CancellationToken.None);
            Assert.Equal(SpendCoinsOutcome.Succeeded, result.Outcome);
            await context.SaveChangesAsync();

            // Simulates a downstream entitlement/outbox failure before the coordinator commits.
            await transaction.RollbackAsync();
        }

        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await verification.Wallets.AsNoTracking()
            .SingleAsync(value => value.UserId == seed.UserId);
        var lot = await verification.PaidCoinLots.AsNoTracking()
            .SingleAsync(value => value.Id == seed.LotIds[0]);

        Assert.Equal(10, wallet.BonusAvailable);
        Assert.Equal(100, wallet.PaidAvailable);
        Assert.Equal(0, lot.SpentCoins);
        Assert.DoesNotContain(
            await verification.WalletOperations.AsNoTracking().ToListAsync(),
            operation => operation.IdempotencyKey == command.IdempotencyKey);
        Assert.Empty(await verification.WalletAllocations.AsNoTracking().ToListAsync());
        await AssertReconciledAsync(verification, seed.UserId);
    }

    [Fact]
    public async Task Engine_defensively_rejects_invalid_amount_inside_transaction()
    {
        var seed = await SeedWalletAsync(0, (100, Now));
        var command = Command(seed.UserId, -1, "invalid-direct-call");

        var result = await SpendInNewScopeAsync(command, Now.AddMinutes(1));

        Assert.Equal(SpendCoinsOutcome.ValidationFailed, result.Outcome);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.DoesNotContain(
            await context.WalletOperations.AsNoTracking().ToListAsync(),
            operation => operation.IdempotencyKey == command.IdempotencyKey);
    }

    [Fact]
    public async Task Database_rejects_allocation_between_different_wallet_owners()
    {
        var first = await SeedWalletAsync(0, (100, Now));
        var second = await SeedWalletAsync(0, (100, Now));
        var foreignOperationId = Guid.NewGuid();

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.WalletOperations.Add(WalletOperation.Create(
                foreignOperationId,
                second.UserId,
                "ChapterUnlock",
                "ReadingUnlock",
                Guid.NewGuid(),
                "foreign-allocation",
                Now.AddMinutes(1)));
            await context.SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, $"""
                INSERT INTO wallet.wallet_allocations
                    (id, operation_id, paid_coin_lot_id, amount_coins, created_at)
                VALUES
                    ('{Guid.NewGuid():D}', '{foreignOperationId:D}',
                     '{first.LotIds[0]:D}', 1, '{Now:O}');
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private async Task<WalletSeed> SeedWalletAsync(
        long bonusCoins,
        params (long Coins, DateTimeOffset CreatedAt)[] paidLots)
    {
        var userId = Guid.NewGuid();
        var wallet = WalletAccount.Create(userId, Now);
        var operations = new List<WalletOperation>();
        var entries = new List<WalletEntry>();
        var lots = new List<PaidCoinLot>();

        if (bonusCoins > 0)
        {
            var bonusOperation = WalletOperation.Create(
                Guid.NewGuid(),
                userId,
                "MissionReward",
                "RewardGrant",
                Guid.NewGuid(),
                $"bonus-{Guid.NewGuid():N}",
                Now);
            wallet.CreditBonus(bonusCoins, Now);
            operations.Add(bonusOperation);
            entries.Add(WalletEntry.Create(
                Guid.NewGuid(),
                bonusOperation,
                WalletCoinTypes.Bonus,
                WalletBuckets.Available,
                bonusCoins,
                1,
                Now));
        }

        foreach (var (coins, createdAt) in paidLots)
        {
            var orderId = Guid.NewGuid();
            var operation = WalletOperation.Create(
                Guid.NewGuid(),
                userId,
                "TopUpCredit",
                "PaymentOrder",
                orderId,
                $"topup-{orderId:N}",
                createdAt);
            var lot = PaidCoinLot.Create(
                Guid.NewGuid(), userId, orderId, coins, createdAt);
            wallet.CreditPaid(coins, createdAt);
            operations.Add(operation);
            entries.Add(WalletEntry.Create(
                Guid.NewGuid(),
                operation,
                WalletCoinTypes.Paid,
                WalletBuckets.Available,
                coins,
                1,
                createdAt));
            lots.Add(lot);
        }

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            $"Wallet Spend User {userId:N}",
            null,
            null,
            Now));
        context.Wallets.Add(wallet);
        context.WalletOperations.AddRange(operations);
        context.WalletEntries.AddRange(entries);
        context.PaidCoinLots.AddRange(lots);
        await context.SaveChangesAsync();

        return new WalletSeed(userId, lots.Select(lot => lot.Id).ToArray());
    }

    private async Task<SpendCoinsResult> SpendInNewScopeAsync(
        SpendCoinsCommand command,
        DateTimeOffset occurredAt,
        ConcurrentStartGate? startGate = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        if (startGate is not null)
        {
            await startGate.ArriveAndWaitAsync();
        }

        var result = await scope.ServiceProvider.GetRequiredService<WalletSpendEngine>()
            .ApplyAsync(command, occurredAt, CancellationToken.None);
        if (result is { Outcome: SpendCoinsOutcome.Succeeded, Receipt.IsReplay: false })
        {
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return result;
    }

    private static SpendCoinsCommand Command(Guid userId, long amount, string identity) => new(
        userId,
        amount,
        "ChapterUnlock",
        "ReadingUnlock",
        Guid.NewGuid(),
        identity);

    private static async Task AssertReconciledAsync(AppDbContext context, Guid userId)
    {
        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(value => value.UserId == userId);
        var lotsAvailable = await context.PaidCoinLots.AsNoTracking()
            .Where(lot => lot.UserId == userId)
            .SumAsync(lot => lot.GrantedCoins - lot.SpentCoins - lot.RefundedCoins - lot.ReservedCoins);
        var lotsReserved = await context.PaidCoinLots.AsNoTracking()
            .Where(lot => lot.UserId == userId)
            .SumAsync(lot => lot.ReservedCoins);
        var paidLedger = await context.WalletEntries.AsNoTracking()
            .Where(entry => entry.UserId == userId &&
                entry.CoinType == WalletCoinTypes.Paid &&
                entry.Bucket == WalletBuckets.Available)
            .SumAsync(entry => entry.DeltaCoins);
        var bonusLedger = await context.WalletEntries.AsNoTracking()
            .Where(entry => entry.UserId == userId &&
                entry.CoinType == WalletCoinTypes.Bonus &&
                entry.Bucket == WalletBuckets.Available)
            .SumAsync(entry => entry.DeltaCoins);

        Assert.Equal(wallet.PaidAvailable, lotsAvailable);
        Assert.Equal(wallet.PaidReserved, lotsReserved);
        Assert.Equal(wallet.PaidAvailable, paidLedger);
        Assert.Equal(wallet.BonusAvailable, bonusLedger);
        Assert.True(wallet.PaidAvailable >= 0);
        Assert.True(wallet.BonusAvailable >= 0);
    }

    private async Task<WalletLockBlocker> HoldWalletLockAsync(Guid userId)
    {
        var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT user_id FROM wallet.wallets WHERE user_id = @user_id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteScalarAsync();
        return new WalletLockBlocker(connection, transaction);
    }

    private static async Task WaitForWalletLockWaitersAsync(
        WalletLockBlocker blocker,
        int expectedWaiters)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT COUNT(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND wait_event_type = 'Lock';
                """,
                blocker.Connection,
                blocker.Transaction);
            var waiters = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (waiters >= expectedWaiters)
            {
                return;
            }

            await Task.Delay(50);
        }

        await using var diagnostics = new NpgsqlCommand(
            """
            SELECT pid, state, COALESCE(wait_event_type, ''), COALESCE(wait_event, ''),
                   replace(left(query, 240), E'\n', ' ')
            FROM pg_stat_activity
            WHERE datname = current_database()
            ORDER BY pid;
            """,
            blocker.Connection,
            blocker.Transaction);
        var activity = new List<string>();
        await using (var reader = await diagnostics.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                activity.Add(string.Join(" | ", Enumerable.Range(0, 5)
                    .Select(index => reader.GetValue(index).ToString())));
            }
        }

        throw new TimeoutException(
            $"Expected {expectedWaiters} Wallet lock waiters were not observed. " +
            string.Join(" || ", activity));
    }

    private static async Task ThrowIfCompletedUnexpectedlyAsync(params Task[] tasks)
    {
        await Task.Delay(200);
        var completed = tasks.FirstOrDefault(task => task.IsCompleted);
        if (completed is not null)
        {
            await completed;
            throw new InvalidOperationException(
                "A concurrent Wallet spend completed before the held Wallet lock was released.");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record WalletSeed(Guid UserId, IReadOnlyList<Guid> LotIds);

    private sealed class ConcurrentStartGate(int participantCount)
    {
        private readonly TaskCompletionSource _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participantCount;

        internal Task ArriveAndWaitAsync()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _allArrived.TrySetResult();
            }

            return _allArrived.Task;
        }
    }

    private sealed class WalletLockBlocker(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) : IAsyncDisposable
    {
        internal NpgsqlConnection Connection { get; } = connection;

        internal NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
