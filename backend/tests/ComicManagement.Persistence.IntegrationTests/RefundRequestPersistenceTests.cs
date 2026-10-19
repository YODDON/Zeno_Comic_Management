using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
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

public sealed class RefundRequestPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_refund_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
    private readonly BlockingRefundObserver _observer = new();
    private ServiceProvider _services = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var services = new ServiceCollection();
        services.AddPersistence(_postgres.GetConnectionString());
        services.RemoveAll<IRefundRequestCriticalSectionObserver>();
        services.AddSingleton<IRefundRequestCriticalSectionObserver>(_observer);
        services.AddSingleton<IRefundRecipientProtector, TestRecipientProtector>();
        _services = services.BuildServiceProvider(validateScopes: true);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();
        _userId = Guid.NewGuid();
        context.Users.Add(User.CreateFromGoogle(
            _userId,
            "Refund Test User",
            null,
            null,
            DateTimeOffset.UtcNow.AddDays(-30)));
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Eligibility_uses_verified_paid_time_and_full_original_lot()
    {
        var eligibleOrder = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-6));
        var expiredOrder = await SeedFulfilledTopUpAsync(
            DateTimeOffset.UtcNow.AddDays(-RefundRequest.RequestWindowDays).AddMinutes(-1));

        var eligible = await EligibilityAsync(eligibleOrder);
        var expired = await EligibilityAsync(expiredOrder);

        Assert.Equal(RefundRequestOutcome.Succeeded, eligible.Outcome);
        Assert.True(eligible.State!.Eligible);
        Assert.Equal(100_000, eligible.State.OriginalAmountVnd);
        Assert.Equal(5_000, eligible.State.FeeVnd);
        Assert.Equal(95_000, eligible.State.NetRefundVnd);
        Assert.Equal(10_000, eligible.State.LotGrantedCoins);
        Assert.Equal(RefundRequestOutcome.WindowExpired, expired.Outcome);
        Assert.False(expired.State!.Eligible);
        Assert.Equal("REFUND_WINDOW_EXPIRED", expired.State.ReasonCode);
    }

    [Fact]
    public async Task Concurrent_same_key_requests_really_contend_and_create_one_request()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var release = _observer.BlockNext();
        var firstTask = CreateAsync(orderId, "same-key", "duplicate transfer");
        await _observer.WaitUntilBlockedAsync();
        var secondTask = CreateAsync(orderId, "same-key", "duplicate transfer");
        await WaitForAdvisoryWaiterAsync();
        release();

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(RefundRequestOutcome.Succeeded, result.Outcome));
        Assert.Equal(results[0].State!.RefundId, results[1].State!.RefundId);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.RefundRequests.CountAsync());
        Assert.Equal(1, await context.RefundRecipients.CountAsync());
        Assert.Empty(await context.WalletEntries.ToListAsync());
        Assert.Empty(await context.WalletOperations.ToListAsync());
    }

    [Fact]
    public async Task Permanent_idempotency_conflicts_and_one_request_per_order_are_enforced()
    {
        var firstOrder = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var secondOrder = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var created = await CreateAsync(firstOrder, "refund-key", "duplicate transfer");

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lot = await context.PaidCoinLots.SingleAsync(item => item.TopUpOrderId == firstOrder);
            lot.Spend(1);
            await context.SaveChangesAsync();
        }

        var replayAfterLotDrift = await CreateAsync(
            firstOrder, "refund-key", "duplicate transfer");
        var differentPayload = await CreateAsync(secondOrder, "refund-key", "other reason");
        var differentKeySameOrder = await CreateAsync(
            firstOrder, "different-key", "duplicate transfer");

        Assert.Equal(RefundRequestOutcome.Succeeded, created.Outcome);
        Assert.Equal(RefundRequestOutcome.Succeeded, replayAfterLotDrift.Outcome);
        Assert.Equal(created.State!.RefundId, replayAfterLotDrift.State!.RefundId);
        Assert.Equal(RefundRequestOutcome.IdempotencyConflict, differentPayload.Outcome);
        Assert.Equal(RefundRequestOutcome.RefundAlreadyExists, differentKeySameOrder.Outcome);
    }

    [Fact]
    public async Task Spent_original_lot_is_ineligible_and_request_has_no_financial_mutation()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lot = await context.PaidCoinLots.SingleAsync(item => item.TopUpOrderId == orderId);
            lot.Spend(1);
            await context.SaveChangesAsync();
        }

        var eligibility = await EligibilityAsync(orderId);
        var create = await CreateAsync(orderId, "spent-lot", "duplicate transfer");

        Assert.Equal(RefundRequestOutcome.RecoveryInsufficient, eligibility.Outcome);
        Assert.Equal("REFUND_RECOVERY_INSUFFICIENT", eligibility.State!.ReasonCode);
        Assert.Equal(RefundRequestOutcome.RecoveryInsufficient, create.Outcome);
        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await verification.RefundRequests.ToListAsync());
        Assert.Empty(await verification.WalletEntries.ToListAsync());
    }

    [Fact]
    public async Task PostgreSql_rejects_invalid_fee_ciphertext_and_cross_user_order()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var ownershipOrderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var created = await CreateAsync(orderId, "constraints", "duplicate transfer");
        var refundId = created.State!.RefundId;

        await AssertCheckViolationAsync(
            $"UPDATE billing.refund_requests SET fee_amount_vnd = 1 WHERE id = '{refundId:D}'");
        await AssertCheckViolationAsync(
            $"UPDATE billing.refund_recipients SET account_number_encrypted = ''::bytea WHERE refund_request_id = '{refundId:D}'");

        var otherUserId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            otherUserId, "Other Refund User", null, null, DateTimeOffset.UtcNow.AddDays(-2)));
        await context.SaveChangesAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO billing.refund_requests
                    (id, payment_order_id, user_id, payment_order_type, reason,
                     original_amount_vnd, fee_amount_vnd, net_refund_vnd,
                     required_recovery_coins, status, idempotency_key,
                     request_fingerprint, requested_at, version)
                VALUES
                    ({Guid.NewGuid()}, {ownershipOrderId}, {otherUserId}, {PaymentOrderTypes.TopUp},
                     {'x'}, {100_000L}, {5_000L}, {95_000L}, {10_000L},
                     {RefundRequestStatuses.Requested}, {'k'}, {new string('a', 64)},
                     {DateTimeOffset.UtcNow}, {1L})
                """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    private async Task<Guid> SeedFulfilledTopUpAsync(DateTimeOffset paidAt)
    {
        paidAt = paidAt.ToUniversalTime();
        var orderId = Guid.NewGuid();
        var order = PaymentOrder.CreateTopUp(
            orderId,
            _userId,
            $"ZENO-{orderId:N}",
            100_000,
            10_000,
            $"seed-{orderId:N}",
            new string('a', 64),
            paidAt.AddMinutes(-10),
            paidAt.AddMinutes(10));
        order.FulfillTopUp(paidAt);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.PaymentOrders.Add(order);
        context.PaidCoinLots.Add(PaidCoinLot.Create(
            Guid.NewGuid(), _userId, orderId, 10_000, paidAt));
        await context.SaveChangesAsync();
        return orderId;
    }

    private async Task<RefundEligibilityResult> EligibilityAsync(Guid orderId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RefundRequestHandler>()
            .GetEligibilityAsync(_userId, orderId);
    }

    private async Task<RefundRequestResult> CreateAsync(
        Guid orderId,
        string key,
        string reason)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RefundRequestHandler>()
            .CreateAsync(
                _userId,
                orderId,
                reason,
                new RefundRecipientInput("VCB", "0123456789", "NGUYEN VAN A"),
                key);
    }

    private async Task WaitForAdvisoryWaiterAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event = 'advisory'", connection);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The duplicate refund request did not contend on the advisory lock.");
    }

    private async Task AssertCheckViolationAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private sealed class TestRecipientProtector : IRefundRecipientProtector
    {
        public ProtectedRefundRecipient Protect(string accountNumber, string accountHolder) =>
            new([1, 2, 3], [4, 5, 6], "test-v1");
    }

    private sealed class BlockingRefundObserver : IRefundRequestCriticalSectionObserver
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _blocked;
        private TaskCompletionSource? _release;

        public Action BlockNext()
        {
            lock (_sync)
            {
                _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return () => _release.TrySetResult();
            }
        }

        public async Task WaitUntilBlockedAsync()
        {
            Task blocked;
            lock (_sync)
            {
                blocked = _blocked?.Task ?? throw new InvalidOperationException();
            }

            await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task AfterIdempotencyLockAcquiredAsync(
            Guid userId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Task? release = null;
            lock (_sync)
            {
                if (_blocked is not null && !_blocked.Task.IsCompleted)
                {
                    _blocked.TrySetResult();
                    release = _release!.Task;
                }
            }

            if (release is not null)
            {
                await release.WaitAsync(cancellationToken);
            }
        }
    }
}
