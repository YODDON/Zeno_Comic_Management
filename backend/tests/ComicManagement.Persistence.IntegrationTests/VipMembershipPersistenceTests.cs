using ComicManagement.Billing.Payments;
using ComicManagement.Identity.Users;
using ComicManagement.Membership.Events;
using ComicManagement.Membership.Memberships;
using ComicManagement.Membership.Plans;
using ComicManagement.Membership.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class VipMembershipPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
            "postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_vip_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Checkout_snapshots_plan_and_replays_permanently_after_expiry()
    {
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Checkout");

        VipMembershipResult first;
        await using (var scope = provider.CreateAsyncScope())
        {
            first = await scope.ServiceProvider
                .GetRequiredService<VipMembershipHandler>()
                .CheckoutAsync(userId, VipPlan.InitialPlanId, "vip-checkout-key");
        }

        Assert.Equal(VipMembershipOutcome.Succeeded, first.Outcome);
        Assert.Equal(29_000, first.Checkout!.AmountVnd);
        Assert.Equal(720, first.Checkout.DurationHours);
        Assert.Equal(TimeSpan.FromMinutes(30),
            first.Checkout.ExpiresAt - await ReadCreatedAtAsync(provider, first.Checkout.OrderId));

        await using (var scope = provider.CreateAsyncScope())
        {
            var conflict = await scope.ServiceProvider
                .GetRequiredService<VipMembershipHandler>()
                .CheckoutAsync(userId, Guid.NewGuid(), "vip-checkout-key");
            Assert.Equal(VipMembershipOutcome.IdempotencyConflict, conflict.Outcome);
        }

        await SetExpiredAsync(provider, first.Checkout.OrderId);
        await using (var scope = provider.CreateAsyncScope())
        {
            var replay = await scope.ServiceProvider
                .GetRequiredService<VipMembershipHandler>()
                .CheckoutAsync(userId, VipPlan.InitialPlanId, "vip-checkout-key");
            Assert.Equal(first.Checkout.OrderId, replay.Checkout!.OrderId);
            Assert.Equal(TopUpOrderStatuses.Expired, replay.Checkout.Status);
        }

        await using var verificationScope = provider.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await context.PaymentOrders.SingleAsync(
            order => order.Id == first.Checkout.OrderId);
        Assert.Equal(TopUpOrderStatuses.Expired, persisted.OrderStatus);
        Assert.NotNull(persisted.ExpiredAt);
        Assert.Equal(1, await context.PaymentOrders.CountAsync(order =>
            order.UserId == userId &&
            order.OrderType == PaymentOrderTypes.VipPurchase &&
            order.IdempotencyKey == "vip-checkout-key"));
    }

    [Fact]
    public async Task Concurrent_same_key_requests_contend_and_create_exactly_one_order()
    {
        var observer = new BlockingCheckoutObserver();
        await using var provider = Services(checkoutObserver: observer)
            .BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Checkout Race");

        observer.BlockNext();
        var firstTask = CheckoutAsync(provider, userId, "vip-race", VipPlan.InitialPlanId);
        await observer.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = CheckoutAsync(provider, userId, "vip-race", VipPlan.InitialPlanId);
        await WaitForAdvisoryWaiterAsync();
        Assert.False(secondTask.IsCompleted);
        observer.Release();

        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.All(results, result =>
            Assert.Equal(VipMembershipOutcome.Succeeded, result.Outcome));
        Assert.Equal(results[0].Checkout!.OrderId, results[1].Checkout!.OrderId);

        var conflict = await CheckoutAsync(
            provider,
            userId,
            "vip-race",
            Guid.NewGuid());
        Assert.Equal(VipMembershipOutcome.IdempotencyConflict, conflict.Outcome);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.PaymentOrders.CountAsync(order =>
            order.UserId == userId &&
            order.OrderType == PaymentOrderTypes.VipPurchase &&
            order.IdempotencyKey == "vip-race"));
    }

    [Fact]
    public async Task Replay_waiting_past_expiry_uses_post_lock_server_time_and_stays_expired()
    {
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Expiry Lock");
        var created = await CheckoutAsync(
            provider,
            userId,
            "vip-expiry-lock",
            VipPlan.InitialPlanId);
        var expiresAt = DateTimeOffset.UtcNow.AddMilliseconds(400);
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE billing.payment_orders
                SET created_at = {expiresAt.AddMinutes(-30)},
                    expires_at = {expiresAt}
                WHERE id = {created.Checkout!.OrderId};
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
                $"{userId:N}:{PaymentOrderTypes.VipPurchase}:vip-expiry-lock");
            await lockCommand.ExecuteScalarAsync();
        }

        var replayTask = CheckoutAsync(
            provider,
            userId,
            "vip-expiry-lock",
            VipPlan.InitialPlanId);
        await WaitForAdvisoryWaiterAsync();
        var delay = expiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        await lockTransaction.CommitAsync();
        var replay = await replayTask;
        Assert.Equal(created.Checkout!.OrderId, replay.Checkout!.OrderId);
        Assert.Equal(TopUpOrderStatuses.Expired, replay.Checkout.Status);

        DateTimeOffset? firstExpiredAt;
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = await context.PaymentOrders.SingleAsync(candidate =>
                candidate.Id == created.Checkout.OrderId);
            firstExpiredAt = order.ExpiredAt;
            Assert.NotNull(firstExpiredAt);
        }

        var monotonic = await CheckoutAsync(
            provider,
            userId,
            "vip-expiry-lock",
            VipPlan.InitialPlanId);
        Assert.Equal(TopUpOrderStatuses.Expired, monotonic.Checkout!.Status);
        await using var verificationScope = provider.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        Assert.Equal(
            firstExpiredAt,
            (await verification.PaymentOrders.SingleAsync(order =>
                order.Id == created.Checkout.OrderId)).ExpiredAt);
    }

    [Fact]
    public async Task New_checkout_samples_server_time_only_after_plan_lock_is_released()
    {
        var observer = new SignalingCheckoutObserver();
        await using var provider = Services(checkoutObserver: observer)
            .BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Plan Lock");
        await using var lockConnection = new NpgsqlConnection(_postgres.GetConnectionString());
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT id FROM membership.vip_plans WHERE is_active FOR UPDATE;",
                         lockConnection,
                         lockTransaction))
        {
            await lockCommand.ExecuteScalarAsync();
        }

        var checkoutTask = CheckoutAsync(
            provider,
            userId,
            "vip-plan-lock",
            VipPlan.InitialPlanId);
        await observer.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.False(checkoutTask.IsCompleted);

        DateTimeOffset beforeRelease;
        await using (var clockCommand = new NpgsqlCommand(
                         "SELECT clock_timestamp();",
                         lockConnection,
                         lockTransaction))
        {
            var value = await clockCommand.ExecuteScalarAsync();
            beforeRelease = value switch
            {
                DateTimeOffset timestamp => timestamp.ToUniversalTime(),
                DateTime timestamp => new DateTimeOffset(timestamp.ToUniversalTime()),
                _ => throw new InvalidOperationException(
                    "PostgreSQL did not return server time."),
            };
        }

        await lockTransaction.CommitAsync();
        var result = await checkoutTask;
        var createdAt = await ReadCreatedAtAsync(provider, result.Checkout!.OrderId);
        Assert.True(createdAt >= beforeRelease);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Checkout.ExpiresAt - createdAt);
    }

    [Fact]
    public async Task Plan_price_change_affects_future_orders_only_and_is_audited()
    {
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Price");
        Guid oldOrderId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<VipMembershipHandler>();
            oldOrderId = (await handler.CheckoutAsync(
                userId,
                VipPlan.InitialPlanId,
                "old-price")).Checkout!.OrderId;
            var updated = await handler.UpdateFutureOrderPriceAsync(
                userId,
                35_000,
                1,
                "V01-PRICE-TEST");
            Assert.Equal(35_000, updated.Plan!.PriceVnd);
            Assert.Equal(2, updated.Plan.Version);
        }

        Guid newOrderId;
        await using (var scope = provider.CreateAsyncScope())
        {
            newOrderId = (await scope.ServiceProvider
                .GetRequiredService<VipMembershipHandler>()
                .CheckoutAsync(userId, VipPlan.InitialPlanId, "new-price"))
                .Checkout!.OrderId;
        }

        await using var verificationScope = provider.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(29_000, (await context.PaymentOrders.FindAsync(oldOrderId))!.AmountVnd);
        Assert.Equal(35_000, (await context.PaymentOrders.FindAsync(newOrderId))!.AmountVnd);
        Assert.Equal(1, await context.AuditLogs.CountAsync(log =>
            log.Action == "Membership.VipPlanPriceChanged" &&
            log.TargetId == VipPlan.InitialPlanId));
    }

    [Fact]
    public async Task Concurrent_distinct_payments_extend_exactly_twice_and_replay_has_no_effect()
    {
        var observer = new BlockingFulfillmentObserver();
        await using var provider = Services(observer).BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Concurrent");
        var firstOrder = await CreateOrderAsync(provider, userId, "vip-concurrent-1");
        var secondOrder = await CreateOrderAsync(provider, userId, "vip-concurrent-2");
        var firstProviderId = "vip-provider-1001";
        var secondProviderId = "vip-provider-1002";

        observer.BlockProvider(firstProviderId);
        var firstTask = IngestAsync(provider, Transaction(
            firstProviderId,
            firstOrder.OrderCode,
            firstOrder.AmountVnd));
        await observer.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = IngestAsync(provider, Transaction(
            secondProviderId,
            secondOrder.OrderCode,
            secondOrder.AmountVnd));
        await Task.Delay(250);
        Assert.False(secondTask.IsCompleted);
        observer.Release();

        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.All(results, result =>
            Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, result.Outcome));

        var replay = await IngestAsync(provider, Transaction(
            firstProviderId,
            firstOrder.OrderCode,
            firstOrder.AmountVnd));
        Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, replay.Outcome);
        Assert.True(replay.IsReplay);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transactions = await context.VipTransactions
            .OrderBy(transaction => transaction.FulfilledAt)
            .ToArrayAsync();
        var subscription = await context.VipSubscriptions.SingleAsync(
            item => item.UserId == userId);
        Assert.Equal(2, transactions.Length);
        Assert.Null(transactions[0].PreviousExpiry);
        Assert.Equal(transactions[0].NewExpiry, transactions[1].PreviousExpiry);
        Assert.Equal(transactions[0].NewExpiry, transactions[1].EffectiveStart);
        Assert.Equal(
            transactions[0].FulfilledAt.AddHours(1_440),
            subscription.ExpiresAt);
        Assert.Equal(transactions[1].NewExpiry, subscription.ExpiresAt);
        Assert.Equal(2, await context.OutboxMessages.CountAsync(message =>
            message.EventType == MembershipEventTypes.VipMembershipActivated));
        Assert.Empty(await context.PaidCoinLots.ToArrayAsync());
        Assert.Empty(await context.WalletOperations.ToArrayAsync());
        Assert.Empty(await context.WalletEntries.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Latest_transaction_uses_subscription_version_when_clock_ties_or_rolls_back(
        bool rollsBack)
    {
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(
            provider,
            rollsBack ? "VIP Clock Rollback" : "VIP Clock Tie");
        var firstOrder = await CreateOrderAsync(provider, userId, $"vip-clock-first-{rollsBack}");
        var secondOrder = await CreateOrderAsync(provider, userId, $"vip-clock-second-{rollsBack}");
        var thirdOrder = await CreateOrderAsync(provider, userId, $"vip-clock-third-{rollsBack}");

        Assert.Equal(
            SePayWebhookStoreOutcome.Fulfilled,
            (await IngestAsync(provider, Transaction(
                $"vip-provider-clock-first-{rollsBack}",
                firstOrder.OrderCode,
                firstOrder.AmountVnd))).Outcome);

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var subscription = await context.VipSubscriptions.SingleAsync(candidate =>
                candidate.UserId == userId);
            var firstTransaction = await context.VipTransactions.SingleAsync(candidate =>
                candidate.UserId == userId);
            var secondFulfilledAt = rollsBack
                ? firstTransaction.FulfilledAt.AddMilliseconds(-1)
                : firstTransaction.FulfilledAt;
            var authoritativeSecondOrder = await context.PaymentOrders.SingleAsync(candidate =>
                candidate.Id == secondOrder.Id);
            var extension = subscription.Extend(
                secondFulfilledAt,
                VipPlan.FixedDurationHours);
            authoritativeSecondOrder.FulfillVipPurchase(secondFulfilledAt);
            context.VipTransactions.Add(VipTransaction.Create(
                Guid.NewGuid(),
                authoritativeSecondOrder.Id,
                userId,
                VipTransaction.VipPurchaseOrderType,
                subscription.Version,
                extension,
                VipPlan.FixedDurationHours,
                secondFulfilledAt));
            await context.SaveChangesAsync();
        }

        var third = await IngestAsync(provider, Transaction(
            $"vip-provider-clock-third-{rollsBack}",
            thirdOrder.OrderCode,
            thirdOrder.AmountVnd));
        Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, third.Outcome);

        await using var verificationScope = provider.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transactions = await verification.VipTransactions
            .Where(candidate => candidate.UserId == userId)
            .OrderBy(candidate => candidate.SubscriptionVersion)
            .ToArrayAsync();
        Assert.Equal([1L, 2L, 3L], transactions.Select(candidate =>
            candidate.SubscriptionVersion).ToArray());
        Assert.Equal(
            transactions[2].NewExpiry,
            (await verification.VipSubscriptions.SingleAsync(candidate =>
                candidate.UserId == userId)).ExpiresAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Expired_or_amount_mismatched_payment_is_reconciled_without_entitlement(
        bool expired)
    {
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, expired ? "VIP Late" : "VIP Mismatch");
        var order = await CreateOrderAsync(provider, userId, $"vip-negative-{expired}");
        if (expired)
        {
            await SetExpiredAsync(provider, order.Id);
        }

        var result = await IngestAsync(provider, Transaction(
            expired ? "vip-provider-late" : "vip-provider-mismatch",
            order.OrderCode,
            expired ? order.AmountVnd : order.AmountVnd + 1_000));

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evidence = await context.ProviderTransactions.SingleAsync(
            item => item.Id == result.ProviderTransactionId);
        Assert.Equal(
            expired
                ? SePayReconciliationReasons.LatePayment
                : SePayReconciliationReasons.AmountMismatch,
            evidence.ReconciliationReason);
        Assert.Empty(await context.VipSubscriptions.ToArrayAsync());
        Assert.Empty(await context.VipTransactions.ToArrayAsync());
        Assert.Empty(await context.OutboxMessages.Where(message =>
            message.EventType == MembershipEventTypes.VipMembershipActivated).ToArrayAsync());
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_order_provider_membership_and_outbox()
    {
        var observer = new ThrowBeforeCommitObserver();
        await using var provider = Services(observer).BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Rollback");
        var order = await CreateOrderAsync(provider, userId, "vip-rollback");

        await Assert.ThrowsAsync<InvalidOperationException>(() => IngestAsync(
            provider,
            Transaction("vip-provider-rollback", order.OrderCode, order.AmountVnd)));

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            TopUpOrderStatuses.Pending,
            (await context.PaymentOrders.FindAsync(order.Id))!.OrderStatus);
        Assert.Empty(await context.ProviderTransactions.ToArrayAsync());
        Assert.Empty(await context.WebhookDeliveries.ToArrayAsync());
        Assert.Empty(await context.VipSubscriptions.ToArrayAsync());
        Assert.Empty(await context.VipTransactions.ToArrayAsync());
        Assert.Empty(await context.OutboxMessages.Where(message =>
            message.EventType == MembershipEventTypes.VipMembershipActivated).ToArrayAsync());
    }

    [Fact]
    public async Task Subscription_history_drift_blocks_fulfillment_fail_closed()
    {
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Drift");
        var order = await CreateOrderAsync(provider, userId, "vip-drift");
        var now = DateTimeOffset.UtcNow;
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO membership.vip_subscriptions
                    (user_id, expires_at, last_fulfilled_at, version)
                VALUES ({userId}, {now.AddHours(720)}, {now}, {1L});
                """);
        }

        var result = await IngestAsync(provider, Transaction(
            "vip-provider-drift",
            order.OrderCode,
            order.AmountVnd));

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);
        await using var verificationScope = provider.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evidence = await verification.ProviderTransactions.SingleAsync(
            item => item.Id == result.ProviderTransactionId);
        Assert.Equal(
            SePayReconciliationReasons.FulfillmentStateConflict,
            evidence.ReconciliationReason);
        Assert.Empty(await verification.VipTransactions.ToArrayAsync());
    }

    [Fact]
    public async Task Missing_subscription_history_version_blocks_fulfillment_fail_closed()
    {
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, "VIP Missing History");
        var firstOrder = await CreateOrderAsync(provider, userId, "vip-history-first");
        var secondOrder = await CreateOrderAsync(provider, userId, "vip-history-second");
        Assert.Equal(
            SePayWebhookStoreOutcome.Fulfilled,
            (await IngestAsync(provider, Transaction(
                "vip-provider-history-first",
                firstOrder.OrderCode,
                firstOrder.AmountVnd))).Outcome);

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.VipSubscriptions
                .Where(candidate => candidate.UserId == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Version, 2));
        }

        var result = await IngestAsync(provider, Transaction(
            "vip-provider-history-second",
            secondOrder.OrderCode,
            secondOrder.AmountVnd));
        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);

        await using var verificationScope = provider.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verification.VipTransactions.CountAsync(candidate =>
            candidate.UserId == userId));
        Assert.Equal(
            TopUpOrderStatuses.Pending,
            (await verification.PaymentOrders.SingleAsync(candidate =>
                candidate.Id == secondOrder.Id)).OrderStatus);
        var evidence = await verification.ProviderTransactions.SingleAsync(candidate =>
            candidate.Id == result.ProviderTransactionId);
        Assert.Equal(
            SePayReconciliationReasons.FulfillmentStateConflict,
            evidence.ReconciliationReason);
        Assert.Equal(1, await verification.OutboxMessages.CountAsync(message =>
            message.EventType == MembershipEventTypes.VipMembershipActivated));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Vip_transaction_history_is_database_append_only(bool update)
    {
        await using var provider = Services().BuildServiceProvider(validateScopes: true);
        var userId = await SeedUserAsync(provider, update ? "VIP Immutable Update" : "VIP Immutable Delete");
        var order = await CreateOrderAsync(provider, userId, $"vip-immutable-{update}");
        var fulfilled = await IngestAsync(provider, Transaction(
            $"vip-provider-immutable-{update}",
            order.OrderCode,
            order.AmountVnd));
        Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, fulfilled.Outcome);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transactionId = await context.VipTransactions
            .Where(item => item.PaymentOrderId == order.Id)
            .Select(item => item.Id)
            .SingleAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            update
                ? context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE membership.vip_transactions
                    SET duration_hours = {1}
                    WHERE id = {transactionId};
                    """)
                : context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM membership.vip_transactions
                    WHERE id = {transactionId};
                    """));

        Assert.Equal("23000", exception.SqlState);
        Assert.Contains("append-only", exception.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    private IServiceCollection Services(
        ISePayFulfillmentCriticalSectionObserver? observer = null,
        IVipCheckoutCriticalSectionObserver? checkoutObserver = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(new VipOrderPolicy(TimeSpan.FromMinutes(30)));
        if (observer is not null)
        {
            services.AddSingleton<ISePayFulfillmentCriticalSectionObserver>(observer);
        }

        if (checkoutObserver is not null)
        {
            services.AddSingleton<IVipCheckoutCriticalSectionObserver>(checkoutObserver);
        }

        services.AddPersistence(_postgres.GetConnectionString());
        return services;
    }

    private static async Task<VipMembershipResult> CheckoutAsync(
        ServiceProvider provider,
        Guid userId,
        string key,
        Guid planId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VipMembershipHandler>()
            .CheckoutAsync(userId, planId, key);
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

        throw new TimeoutException("The VIP checkout request did not wait for the advisory lock.");
    }

    private static async Task<Guid> SeedUserAsync(
        ServiceProvider provider,
        string displayName)
    {
        var userId = Guid.NewGuid();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            displayName,
            null,
            null,
            DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        return userId;
    }

    private static async Task<PaymentOrder> CreateOrderAsync(
        ServiceProvider provider,
        Guid userId,
        string key)
    {
        Guid orderId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider
                .GetRequiredService<VipMembershipHandler>()
                .CheckoutAsync(userId, VipPlan.InitialPlanId, key);
            orderId = result.Checkout!.OrderId;
        }

        await using var readScope = provider.CreateAsyncScope();
        return await readScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PaymentOrders
            .AsNoTracking()
            .SingleAsync(order => order.Id == orderId);
    }

    private static async Task<SePayWebhookStoreResult> IngestAsync(
        ServiceProvider provider,
        SePayNormalizedTransaction transaction)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISePayWebhookStore>()
            .IngestAsync(transaction, DateTimeOffset.UtcNow);
    }

    private static SePayNormalizedTransaction Transaction(
        string providerId,
        string orderCode,
        long amountVnd) => new(
        providerId,
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{providerId}|{orderCode}|{amountVnd}"))),
        amountVnd,
        ProviderTransactionDirections.Incoming,
        orderCode,
        "6789",
        $"REF-{providerId}",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        null);

    private static async Task<DateTimeOffset> ReadCreatedAtAsync(
        ServiceProvider provider,
        Guid orderId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PaymentOrders
            .Where(order => order.Id == orderId)
            .Select(order => order.CreatedAt)
            .SingleAsync();
    }

    private static async Task SetExpiredAsync(ServiceProvider provider, Guid orderId)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE billing.payment_orders
            SET created_at = {expiresAt.AddMinutes(-30)},
                expires_at = {expiresAt}
            WHERE id = {orderId};
            """);
    }

    private sealed class BlockingFulfillmentObserver :
        ISePayFulfillmentCriticalSectionObserver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _blockedProvider;

        internal Task Entered => _entered.Task;

        internal void BlockProvider(string providerId) => _blockedProvider = providerId;

        internal void Release() => _release.TrySetResult();

        public async Task AfterFinancialLocksAcquiredAsync(
            string providerTransactionId,
            Guid paymentOrderId,
            CancellationToken cancellationToken)
        {
            if (providerTransactionId != _blockedProvider)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public Task BeforeFulfillmentCommitAsync(
            string providerTransactionId,
            Guid paymentOrderId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingCheckoutObserver : IVipCheckoutCriticalSectionObserver
    {
        private TaskCompletionSource _entered = NewSignal();
        private TaskCompletionSource _release = NewSignal();
        private int _shouldBlock;

        internal Task Entered => _entered.Task;

        internal void BlockNext()
        {
            _entered = NewSignal();
            _release = NewSignal();
            Interlocked.Exchange(ref _shouldBlock, 1);
        }

        internal void Release() => _release.TrySetResult();

        public async Task AfterLockAcquiredAsync(
            Guid userId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _shouldBlock, 0) == 0)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SignalingCheckoutObserver : IVipCheckoutCriticalSectionObserver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        public Task AfterLockAcquiredAsync(
            Guid userId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowBeforeCommitObserver :
        ISePayFulfillmentCriticalSectionObserver
    {
        public Task AfterFinancialLocksAcquiredAsync(
            string providerTransactionId,
            Guid paymentOrderId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task BeforeFulfillmentCommitAsync(
            string providerTransactionId,
            Guid paymentOrderId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated V01 commit failure.");
    }
}
