using ComicManagement.Billing.Payments;
using ComicManagement.Identity.Users;
using ComicManagement.Persistence;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class TopUpOrderPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset InitialNow =
        new(2026, 10, 15, 13, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_billing_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private readonly ControllableCriticalSectionObserver _criticalSection = new();
    private ServiceProvider _services = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var services = new ServiceCollection();
        services.AddPersistence(_postgres.GetConnectionString());
        services.RemoveAll<ITopUpOrderCriticalSectionObserver>();
        services.AddSingleton<ITopUpOrderCriticalSectionObserver>(_criticalSection);
        services.AddSingleton(new TopUpOrderPolicy(TimeSpan.FromMinutes(30)));
        _services = services.BuildServiceProvider(validateScopes: true);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();
        _userId = Guid.NewGuid();
        context.Users.Add(User.CreateFromGoogle(
            _userId,
            "Billing Test User",
            null,
            null,
            InitialNow));
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_identical_requests_create_exactly_one_order_and_replay_it()
    {
        var release = _criticalSection.BlockNextAcquisition();
        var firstRequest = CreateAsync(10_000, "same-key");
        await _criticalSection.WaitUntilAcquiredAsync();
        var secondRequest = CreateAsync(10_000, "same-key");
        await WaitForAdvisoryWaiterAsync();
        release();
        var results = await Task.WhenAll(firstRequest, secondRequest);

        Assert.All(results, result =>
            Assert.Equal(TopUpOrderOutcome.Succeeded, result.Outcome));
        Assert.Equal(results[0].State!.OrderId, results[1].State!.OrderId);
        Assert.Equal(1, await CountOrdersAsync());
    }

    [Fact]
    public async Task Concurrent_different_payloads_with_same_key_return_one_conflict()
    {
        var release = _criticalSection.BlockNextAcquisition();
        var firstRequest = CreateAsync(10_000, "conflicting-key");
        await _criticalSection.WaitUntilAcquiredAsync();
        var secondRequest = CreateAsync(20_000, "conflicting-key");
        await WaitForAdvisoryWaiterAsync();
        release();
        var results = await Task.WhenAll(firstRequest, secondRequest);

        Assert.Single(results, result => result.Outcome == TopUpOrderOutcome.Succeeded);
        Assert.Single(results, result =>
            result.Outcome == TopUpOrderOutcome.IdempotencyConflict);
        Assert.Equal(1, await CountOrdersAsync());
    }

    [Fact]
    public async Task Expired_order_replays_without_resetting_idempotency()
    {
        var created = await CreateAsync(29_000, "expiry-key");
        var expiresAt = DateTimeOffset.UtcNow.AddMilliseconds(400);
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE billing.payment_orders
                SET created_at = {expiresAt.AddMinutes(-30)},
                    expires_at = {expiresAt}
                WHERE id = {created.State!.OrderId};
                """);
        }

        await using var lockConnection = new NpgsqlConnection(_postgres.GetConnectionString());
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));",
                         lockConnection,
                         lockTransaction))
        {
            lockCommand.Parameters.AddWithValue(
                "lock_key",
                $"{_userId:N}:{PaymentOrderTypes.TopUp}:expiry-key");
            await lockCommand.ExecuteScalarAsync();
        }

        var replayTask = CreateAsync(29_000, "expiry-key");
        await WaitForAdvisoryWaiterAsync();
        var delay = expiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        await lockTransaction.CommitAsync();
        var replay = await replayTask;

        Assert.Equal(created.State!.OrderId, replay.State!.OrderId);
        Assert.Equal(TopUpOrderStatuses.Expired, replay.State.Status);

        DateTimeOffset? expiredAt;
        await using (var verificationScope = _services.CreateAsyncScope())
        {
            var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await context.PaymentOrders.SingleAsync(
                order => order.Id == replay.State.OrderId);
            Assert.Equal(TopUpOrderStatuses.Expired, persisted.OrderStatus);
            Assert.NotNull(persisted.ExpiredAt);
            expiredAt = persisted.ExpiredAt;
        }

        var monotonicReplay = await CreateAsync(29_000, "expiry-key");
        Assert.Equal(TopUpOrderStatuses.Expired, monotonicReplay.State!.Status);
        await using (var verificationScope = _services.CreateAsyncScope())
        {
            var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await context.PaymentOrders.SingleAsync(
                order => order.Id == replay.State.OrderId);
            Assert.Equal(expiredAt, persisted.ExpiredAt);
        }

        var conflict = await CreateAsync(30_000, "expiry-key");
        Assert.Equal(TopUpOrderOutcome.IdempotencyConflict, conflict.Outcome);
        Assert.Equal(1, await CountOrdersAsync());
    }

    [Fact]
    public async Task Same_key_is_scoped_to_user_and_operation()
    {
        var anotherUserId = Guid.NewGuid();
        await using (var seedScope = _services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Users.Add(User.CreateFromGoogle(
                anotherUserId,
                "Another Billing User",
                null,
                null,
                InitialNow));
            await context.SaveChangesAsync();
        }

        var first = await CreateAsync(10_000, "shared-key", _userId);
        var second = await CreateAsync(10_000, "shared-key", anotherUserId);

        Assert.NotEqual(first.State!.OrderId, second.State!.OrderId);
        Assert.Equal(2, await CountOrdersAsync());
    }

    [Fact]
    public async Task PostgreSql_rejects_invalid_topup_amount_status_and_fingerprint()
    {
        var created = await CreateAsync(10_000, "database-checks");
        var orderId = created.State!.OrderId;

        await AssertCheckViolationAsync(
            $"UPDATE billing.payment_orders SET amount_vnd = 10500 WHERE id = '{orderId:D}'");
        await AssertCheckViolationAsync(
            $"UPDATE billing.payment_orders SET order_status = 'Expired' WHERE id = '{orderId:D}'");
        await AssertCheckViolationAsync(
            $"UPDATE billing.payment_orders SET request_fingerprint = 'invalid' WHERE id = '{orderId:D}'");
    }

    [Fact]
    public async Task Paid_lot_database_relation_rejects_cross_user_order_ownership()
    {
        var created = await CreateAsync(10_000, "ownership-order");
        var orderId = created.State!.OrderId;
        var otherUserId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            otherUserId,
            "Other Lot Owner",
            null,
            null,
            InitialNow));
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO wallet.paid_coin_lots
                    (id, user_id, topup_order_id, granted_coins, spent_coins,
                     refunded_coins, reserved_coins, created_at, version)
                VALUES
                    ({Guid.NewGuid()}, {otherUserId}, {orderId}, {1_000L}, {0L},
                     {0L}, {0L}, {InitialNow}, {1L});
                """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Persistence_boundary_rejects_paid_lot_for_vip_purchase_order()
    {
        var vipOrderId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO billing.payment_orders
                (id, user_id, order_code, order_type, amount_vnd, paid_coins_snapshot,
                 vip_plan_id, vip_duration_hours_snapshot, vip_discount_percent_snapshot,
                 provider, provider_payment_status, fulfillment_status, idempotency_key,
                 request_fingerprint, order_status, expires_at, expired_at,
                 verified_paid_at, fulfilled_at, created_at, version)
            VALUES
                ({vipOrderId}, {_userId}, {$"VIP-{vipOrderId:N}"},
                 {PaymentOrderTypes.VipPurchase}, {29_000L}, NULL, {Guid.NewGuid()},
                 {720}, {20}, {PaymentProviders.SePay}, {ProviderPaymentStatuses.Pending},
                 {PaymentFulfillmentStatuses.NotStarted}, {$"vip-{vipOrderId:N}"},
                 {new string('c', 64)}, {TopUpOrderStatuses.Pending},
                 {InitialNow.AddMinutes(30)}, NULL, NULL, NULL, {InitialNow}, {1L});
            """);
        context.PaidCoinLots.Add(PaidCoinLot.Create(
            Guid.NewGuid(),
            _userId,
            vipOrderId,
            1_000,
            InitialNow));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesAsync());
        Assert.Contains("TopUp PaymentOrder", exception.Message, StringComparison.Ordinal);
    }

    private async Task<TopUpOrderResult> CreateAsync(
        long amountVnd,
        string idempotencyKey,
        Guid? userId = null)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<TopUpOrderHandler>()
            .CreateAsync(userId ?? _userId, amountVnd, idempotencyKey);
    }

    private async Task<int> CountOrdersAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .PaymentOrders
            .CountAsync();
    }

    private async Task AssertCheckViolationAsync(string sql)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private async Task WaitForAdvisoryWaiterAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND NOT granted;
                """,
                connection);
            if (Convert.ToInt32(
                    await command.ExecuteScalarAsync(),
                    System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The second TopUp request did not wait for the advisory lock.");
    }

    private sealed class ControllableCriticalSectionObserver :
        ITopUpOrderCriticalSectionObserver
    {
        private TaskCompletionSource _acquired = NewSignal();
        private TaskCompletionSource _release = NewSignal();
        private int _shouldBlock;

        internal Action BlockNextAcquisition()
        {
            _acquired = NewSignal();
            _release = NewSignal();
            Interlocked.Exchange(ref _shouldBlock, 1);
            return () => _release.TrySetResult();
        }

        internal Task WaitUntilAcquiredAsync() => _acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async Task AfterLockAcquiredAsync(
            Guid userId,
            string orderType,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _shouldBlock, 0) == 0)
            {
                return;
            }

            _acquired.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
