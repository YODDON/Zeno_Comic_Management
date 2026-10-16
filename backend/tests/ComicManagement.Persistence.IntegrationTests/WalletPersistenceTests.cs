using ComicManagement.Identity.Users;
using ComicManagement.Billing.Payments;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class WalletPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_wallet_tests")
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
    public async Task Concurrent_first_reads_create_exactly_one_zero_wallet()
    {
        var userId = await CreateUserAsync();
        var reads = Enumerable.Range(0, 8)
            .Select(_ => ReadBalanceInNewScopeAsync(userId))
            .ToArray();

        var balances = await Task.WhenAll(reads);

        Assert.All(balances, balance =>
        {
            Assert.Equal(0, balance.PaidAvailable);
            Assert.Equal(0, balance.PaidReserved);
            Assert.Equal(0, balance.BonusAvailable);
            Assert.Equal(0, balance.TotalAvailable);
            Assert.Equal(1, balance.Version);
        });

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.Wallets.CountAsync(wallet => wallet.UserId == userId));
    }

    [Fact]
    public async Task Wallet_lots_and_ledger_reconcile_and_keep_spent_separate_from_refunded()
    {
        var userId = await CreateUserAsync();
        var firstTopUpOrder = CreateTopUpOrder(userId, "wallet-topup-1", new string('a', 64));
        var secondTopUpOrder = CreateTopUpOrder(userId, "wallet-topup-2", new string('b', 64));
        var topUpOperation = WalletOperation.Create(
            Guid.NewGuid(), userId, "TopUpCredit", "PaymentOrder", firstTopUpOrder.Id, "topup-1", Now);
        var missionOperation = WalletOperation.Create(
            Guid.NewGuid(), userId, "MissionReward", "RewardGrant", Guid.NewGuid(), "mission-1", Now);
        var secondTopUpOperation = WalletOperation.Create(
            Guid.NewGuid(), userId, "TopUpCredit", "PaymentOrder", secondTopUpOrder.Id, "topup-2", Now);
        var spendOperation = WalletOperation.Create(
            Guid.NewGuid(), userId, "ChapterUnlock", "Unlock", Guid.NewGuid(), "unlock-1", Now);
        var refundOperation = WalletOperation.Create(
            Guid.NewGuid(), userId, "TopUpRefund", "Refund", Guid.NewGuid(), "refund-1", Now);
        var paidEntry = WalletEntry.Create(
            Guid.NewGuid(), topUpOperation, WalletCoinTypes.Paid, WalletBuckets.Available, 1_000, 1, Now);
        var bonusEntry = WalletEntry.Create(
            Guid.NewGuid(), missionOperation, WalletCoinTypes.Bonus, WalletBuckets.Available, 20, 1, Now);
        var secondPaidEntry = WalletEntry.Create(
            Guid.NewGuid(), secondTopUpOperation, WalletCoinTypes.Paid, WalletBuckets.Available, 1_000, 1, Now);
        var spentEntry = WalletEntry.Create(
            Guid.NewGuid(), spendOperation, WalletCoinTypes.Paid, WalletBuckets.Available, -20, 1, Now);
        var refundedEntry = WalletEntry.Create(
            Guid.NewGuid(), refundOperation, WalletCoinTypes.Paid, WalletBuckets.Available, -30, 1, Now);
        var wallet = WalletAccount.Create(userId, Now);
        wallet.CreditPaid(1_950, Now);
        wallet.CreditBonus(20, Now);
        var activeLot = PaidCoinLot.Create(
            Guid.NewGuid(), userId, firstTopUpOrder.Id, 1_000, Now);
        var historicalLot = PaidCoinLot.Create(
            Guid.NewGuid(), userId, secondTopUpOrder.Id, 1_000, Now.AddSeconds(1));
        historicalLot.Spend(20);
        historicalLot.Reserve(30);
        historicalLot.RefundReserved(30);

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.PaymentOrders.AddRange(firstTopUpOrder, secondTopUpOrder);
            context.Wallets.Add(wallet);
            context.PaidCoinLots.AddRange(activeLot, historicalLot);
            context.WalletOperations.AddRange(
                topUpOperation,
                missionOperation,
                secondTopUpOperation,
                spendOperation,
                refundOperation);
            context.WalletEntries.AddRange(
                paidEntry,
                bonusEntry,
                secondPaidEntry,
                spentEntry,
                refundedEntry);
            await context.SaveChangesAsync();
        }

        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedWallet = await verification.Wallets.AsNoTracking()
            .SingleAsync(value => value.UserId == userId);
        var availablePaidLots = await verification.PaidCoinLots.AsNoTracking()
            .Where(lot => lot.UserId == userId)
            .SumAsync(lot => lot.GrantedCoins - lot.SpentCoins - lot.RefundedCoins - lot.ReservedCoins);
        var ledgerPaid = await verification.WalletEntries.AsNoTracking()
            .Where(entry => entry.UserId == userId &&
                entry.CoinType == WalletCoinTypes.Paid &&
                entry.Bucket == WalletBuckets.Available)
            .SumAsync(entry => entry.DeltaCoins);
        var ledgerBonus = await verification.WalletEntries.AsNoTracking()
            .Where(entry => entry.UserId == userId &&
                entry.CoinType == WalletCoinTypes.Bonus)
            .SumAsync(entry => entry.DeltaCoins);
        var persistedHistoricalLot = await verification.PaidCoinLots.AsNoTracking()
            .SingleAsync(lot => lot.Id == historicalLot.Id);
        var projectedEntries = await verificationScope.ServiceProvider
            .GetRequiredService<IWalletQueryStore>()
            .ListEntriesAsync(userId, null, 10);

        Assert.Equal(persistedWallet.PaidAvailable, availablePaidLots);
        Assert.Equal(persistedWallet.PaidAvailable, ledgerPaid);
        Assert.Equal(persistedWallet.BonusAvailable, ledgerBonus);
        Assert.Equal(20, persistedHistoricalLot.SpentCoins);
        Assert.Equal(30, persistedHistoricalLot.RefundedCoins);
        Assert.Equal(0, persistedHistoricalLot.ReservedCoins);
        Assert.Equal(950, persistedHistoricalLot.AvailableCoins);
        Assert.Equal(5, projectedEntries.Count);
    }

    [Fact]
    public async Task Database_rejects_negative_balances_lot_overdraw_and_duplicate_operation_identity()
    {
        var userId = await CreateUserAsync();
        await ReadBalanceInNewScopeAsync(userId);

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();

        var negative = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            $"UPDATE wallet.wallets SET paid_available = -1 WHERE user_id = '{userId:D}'"));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, negative.SqlState);
        Assert.Equal("ck_wallets_paid_available", negative.ConstraintName);

        var lotId = Guid.NewGuid();
        var topUpOrderId = Guid.NewGuid();
        var orphanLot = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            $"""
            INSERT INTO wallet.paid_coin_lots
                (id, user_id, topup_order_id, topup_order_type, granted_coins, spent_coins,
                 refunded_coins, reserved_coins, created_at, version)
            VALUES
                ('{Guid.NewGuid():D}', '{userId:D}', '{Guid.NewGuid():D}', 'TopUp', 10, 0, 0, 0, NOW(), 1)
            """));
        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, orphanLot.SqlState);
        Assert.Equal("fk_paid_coin_lots_topup_payment_orders", orphanLot.ConstraintName);

        await ExecuteAsync(connection, $"""
            INSERT INTO billing.payment_orders
                (id, user_id, order_code, order_type, amount_vnd, paid_coins_snapshot,
                 provider, provider_payment_status, fulfillment_status, idempotency_key,
                 request_fingerprint, order_status, expires_at, created_at, version)
            VALUES
                ('{topUpOrderId:D}', '{userId:D}', 'ZENO-{topUpOrderId:N}', 'TopUp',
                 10000, 1000, 'SePay', 'Pending', 'NotStarted', 'wallet-db-test',
                 '{new string('c', 64)}', 'Pending', NOW() + interval '30 minutes', NOW(), 1)
            """);
        await ExecuteAsync(connection, $"""
            INSERT INTO wallet.paid_coin_lots
                (id, user_id, topup_order_id, topup_order_type, granted_coins, spent_coins,
                 refunded_coins, reserved_coins, created_at, version)
            VALUES
                ('{lotId:D}', '{userId:D}', '{topUpOrderId:D}', 'TopUp', 10, 0, 0, 0, NOW(), 1)
            """);
        var overdraw = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            $"UPDATE wallet.paid_coin_lots SET spent_coins = 11 WHERE id = '{lotId:D}'"));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, overdraw.SqlState);
        Assert.Equal("ck_paid_coin_lots_capacity", overdraw.ConstraintName);

        var sourceId = Guid.NewGuid();
        await ExecuteAsync(connection, $"""
            INSERT INTO wallet.wallet_operations
                (id, user_id, operation_type, source_type, source_id, idempotency_key, created_at)
            VALUES
                ('{Guid.NewGuid():D}', '{userId:D}', 'TopUpCredit', 'PaymentOrder',
                 '{sourceId:D}', 'key-1', NOW())
            """);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            $"""
            INSERT INTO wallet.wallet_operations
                (id, user_id, operation_type, source_type, source_id, idempotency_key, created_at)
            VALUES
                ('{Guid.NewGuid():D}', '{userId:D}', 'TopUpCredit', 'PaymentOrder',
                 '{sourceId:D}', 'key-2', NOW())
            """));
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("ux_wallet_operations_business_source", duplicate.ConstraintName);

        var duplicateIdempotency = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            $"""
            INSERT INTO wallet.wallet_operations
                (id, user_id, operation_type, source_type, source_id, idempotency_key, created_at)
            VALUES
                ('{Guid.NewGuid():D}', '{userId:D}', 'TopUpCredit', 'PaymentOrder',
                 '{Guid.NewGuid():D}', 'key-1', NOW())
            """));
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, duplicateIdempotency.SqlState);
        Assert.Equal(
            "ux_wallet_operations_user_type_idempotency",
            duplicateIdempotency.ConstraintName);
    }

    [Fact]
    public async Task Ledger_is_append_only_in_EF_and_direct_PostgreSQL()
    {
        var userId = await CreateUserAsync();
        var operation = WalletOperation.Create(
            Guid.NewGuid(), userId, "TestCredit", "TestSource", Guid.NewGuid(), null, Now);
        var entry = WalletEntry.Create(
            Guid.NewGuid(), operation, WalletCoinTypes.Paid, WalletBuckets.Available, 1, 1, Now);

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.WalletOperations.Add(operation);
            context.WalletEntries.Add(entry);
            await context.SaveChangesAsync();
            context.WalletEntries.Remove(entry);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
            Assert.Equal("Wallet ledger records are append-only.", exception.Message);
        }

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var mutation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            $"UPDATE wallet.wallet_entries SET delta_coins = 2 WHERE id = '{entry.Id:D}'"));
        Assert.Equal("55000", mutation.SqlState);
    }

    private async Task<Guid> CreateUserAsync()
    {
        var userId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            $"Wallet User {userId:N}",
            null,
            null,
            Now));
        await context.SaveChangesAsync();
        return userId;
    }

    private async Task<WalletBalanceState> ReadBalanceInNewScopeAsync(Guid userId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWalletQueryStore>()
            .GetOrCreateBalanceAsync(userId, Now);
    }

    private static PaymentOrder CreateTopUpOrder(
        Guid userId,
        string idempotencyKey,
        string fingerprint)
    {
        var id = Guid.NewGuid();
        return PaymentOrder.CreateTopUp(
            id,
            userId,
            $"ZENO-{id:N}",
            10_000,
            1_000,
            idempotencyKey,
            fingerprint,
            Now,
            Now.AddMinutes(30));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
