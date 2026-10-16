using ComicManagement.Billing.Events;
using ComicManagement.Billing.Payments;
using ComicManagement.Identity.Users;
using ComicManagement.Persistence;
using ComicManagement.Shared.Messaging;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using System.Text.Json;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class TopUpFulfillmentPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset VerifiedAt =
        new(2026, 10, 16, 4, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_fulfillment_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Exact_payment_credits_wallet_lot_ledger_and_outbox_once()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var payment = Transaction("b03-exact", seeded.OrderCode, 100_000, 'a');

        var first = await IngestAsync(provider, payment);
        var replay = await IngestAsync(provider, payment);

        Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, first.Outcome);
        Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, replay.Outcome);
        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.ProviderTransactionId, replay.ProviderTransactionId);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await context.PaymentOrders.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);
        Assert.Equal(TopUpOrderStatuses.Succeeded, order.OrderStatus);
        Assert.Equal(ProviderPaymentStatuses.Verified, order.ProviderPaymentStatus);
        Assert.Equal(PaymentFulfillmentStatuses.Fulfilled, order.FulfillmentStatus);
        Assert.NotNull(order.VerifiedPaidAt);
        Assert.Equal(order.VerifiedPaidAt, order.FulfilledAt);

        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == seeded.UserId);
        Assert.Equal(10_000, wallet.PaidAvailable);
        var lot = await context.PaidCoinLots.AsNoTracking()
            .SingleAsync(candidate => candidate.TopUpOrderId == seeded.OrderId);
        Assert.Equal(10_000, lot.GrantedCoins);
        Assert.Equal(seeded.UserId, lot.UserId);
        Assert.Equal(PaymentOrderTypes.TopUp, lot.TopUpOrderType);

        var operation = await context.WalletOperations.AsNoTracking()
            .SingleAsync(candidate => candidate.SourceId == seeded.OrderId);
        Assert.Equal(WalletOperationTypes.TopUpCredit, operation.OperationType);
        Assert.Equal(WalletOperationSourceTypes.PaymentOrder, operation.SourceType);
        var entry = await context.WalletEntries.AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operation.Id);
        Assert.Equal(10_000, entry.DeltaCoins);
        Assert.Equal(WalletCoinTypes.Paid, entry.CoinType);
        Assert.Equal(WalletBuckets.Available, entry.Bucket);

        var providerTransaction = await context.ProviderTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == first.ProviderTransactionId);
        Assert.Equal(
            ProviderTransactionProcessingStatuses.Fulfilled,
            providerTransaction.ProcessingStatus);
        Assert.Equal(seeded.OrderId, providerTransaction.MatchedOrderId);
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(message =>
                message.EventType == BillingEventTypes.WalletTopUpCompleted));
        var outbox = await context.OutboxMessages.AsNoTracking()
            .SingleAsync(message =>
                message.EventType == BillingEventTypes.WalletTopUpCompleted);
        var payload = JsonSerializer.Deserialize<WalletTopUpCompletedEvent>(
            outbox.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(seeded.OrderId, payload.PaymentOrderId);
        Assert.Equal(first.ProviderTransactionId, payload.ProviderTransactionId);
        Assert.Equal(seeded.UserId, payload.UserId);
        Assert.Equal(100_000, payload.AmountVnd);
        Assert.Equal(10_000, payload.PaidCoins);
        Assert.Equal(order.FulfilledAt, payload.FulfilledAt);
        Assert.Equal(2, await context.WebhookDeliveries.CountAsync(delivery =>
            delivery.ProviderTransactionId == first.ProviderTransactionId));
        Assert.Equal(1, await context.WebhookDeliveries.CountAsync(delivery =>
            delivery.ProviderTransactionId == first.ProviderTransactionId &&
            delivery.ProcessingResult == WebhookDeliveryProcessingResults.Duplicate));
    }

    [Fact]
    public async Task Amount_mismatch_is_durable_reconciliation_without_credit()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));

        var result = await IngestAsync(
            provider,
            Transaction("b03-amount", seeded.OrderCode, 101_000, 'b'));

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);
        await AssertNoFinancialCreditAsync(
            provider,
            seeded,
            result.ProviderTransactionId,
            SePayReconciliationReasons.AmountMismatch,
            TopUpOrderStatuses.Pending);
    }

    [Fact]
    public async Task Occurred_before_expiry_but_received_after_expiry_is_late_and_never_credited()
    {
        await using var provider = await CreateProviderAsync();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var seeded = await SeedOrderAsync(provider, 100_000, expiresAt);
        var payment = Transaction(
            "b03-late",
            seeded.OrderCode,
            100_000,
            'c',
            occurredAt: expiresAt.AddMinutes(-1));

        var first = await IngestAsync(provider, payment);
        var replay = await IngestAsync(provider, payment);

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, first.Outcome);
        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, replay.Outcome);
        Assert.True(replay.IsReplay);
        await AssertNoFinancialCreditAsync(
            provider,
            seeded,
            first.ProviderTransactionId,
            SePayReconciliationReasons.LatePayment,
            TopUpOrderStatuses.Expired);
    }

    [Fact]
    public async Task Authoritative_expiry_takes_precedence_over_amount_mismatch()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await IngestAsync(
            provider,
            Transaction("b03-late-mismatch", seeded.OrderCode, 101_000, '4'));

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);
        await AssertNoFinancialCreditAsync(
            provider,
            seeded,
            result.ProviderTransactionId,
            SePayReconciliationReasons.LatePayment,
            TopUpOrderStatuses.Expired);
    }

    [Fact]
    public async Task Server_time_is_sampled_after_financial_lock_wait_and_closes_expired_order()
    {
        var observer = new BlockingFulfillmentObserver();
        await using var provider = await CreateProviderAsync(observer);
        var expiresAt = DateTimeOffset.UtcNow.AddMilliseconds(800);
        var seeded = await SeedOrderAsync(provider, 100_000, expiresAt);
        var task = IngestAsync(
            provider,
            Transaction("b03-cross-expiry", seeded.OrderCode, 100_000, 'd'));
        await observer.FirstLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var delay = expiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        observer.ReleaseFirst.TrySetResult();
        var result = await task;

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);
        await AssertNoFinancialCreditAsync(
            provider,
            seeded,
            result.ProviderTransactionId,
            SePayReconciliationReasons.LatePayment,
            TopUpOrderStatuses.Expired);
    }

    [Fact]
    public async Task Concurrent_distinct_payments_for_one_order_really_contend_and_credit_once()
    {
        var observer = new BlockingFulfillmentObserver();
        await using var provider = await CreateProviderAsync(observer);
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));

        var firstTask = IngestAsync(
            provider,
            Transaction("b03-race-a", seeded.OrderCode, 100_000, 'e'));
        await observer.FirstLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = IngestAsync(
            provider,
            Transaction("b03-race-b", seeded.OrderCode, 100_000, 'f'));
        await WaitForDatabaseLockWaiterAsync();
        Assert.Equal(1, observer.EntryCount);
        Assert.False(secondTask.IsCompleted);

        observer.ReleaseFirst.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => result.Outcome == SePayWebhookStoreOutcome.Fulfilled);
        Assert.Single(results, result =>
            result.Outcome == SePayWebhookStoreOutcome.ReconciliationRequired);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.PaidCoinLots.CountAsync(lot =>
            lot.TopUpOrderId == seeded.OrderId));
        Assert.Equal(1, await context.WalletOperations.CountAsync(operation =>
            operation.SourceId == seeded.OrderId));
        Assert.Equal(1, await context.OutboxMessages.CountAsync(message =>
            message.EventType == BillingEventTypes.WalletTopUpCompleted));
        Assert.Equal(10_000, (await context.Wallets.AsNoTracking()
            .SingleAsync(wallet => wallet.UserId == seeded.UserId)).PaidAvailable);
        Assert.Equal(1, await context.ProviderTransactions.CountAsync(candidate =>
            candidate.MatchedOrderId == seeded.OrderId &&
            candidate.ProcessingStatus == ProviderTransactionProcessingStatuses.Fulfilled));
        Assert.Equal(1, await context.ProviderTransactions.CountAsync(candidate =>
            candidate.MatchedOrderId == seeded.OrderId &&
            candidate.ReconciliationReason ==
                SePayReconciliationReasons.OrderAlreadyFulfilled));
    }

    [Fact]
    public async Task Concurrent_same_provider_payment_serializes_and_replays_one_credit()
    {
        var observer = new BlockingWebhookObserver();
        await using var provider = await CreateProviderAsync(webhookObserver: observer);
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var payment = Transaction(
            "b03-duplicate-race",
            seeded.OrderCode,
            100_000,
            '2');

        var firstTask = IngestAsync(provider, payment);
        await observer.FirstLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = IngestAsync(provider, payment);
        await WaitForDatabaseLockWaiterAsync();
        Assert.Equal(1, observer.EntryCount);
        Assert.False(secondTask.IsCompleted);
        observer.ReleaseFirst.TrySetResult();

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(
            SePayWebhookStoreOutcome.Fulfilled,
            result.Outcome));
        Assert.Single(results, result => !result.IsReplay);
        Assert.Single(results, result => result.IsReplay);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.PaidCoinLots.CountAsync(lot =>
            lot.TopUpOrderId == seeded.OrderId));
        Assert.Equal(1, await context.WalletOperations.CountAsync(operation =>
            operation.SourceId == seeded.OrderId));
        Assert.Equal(1, await context.OutboxMessages.CountAsync(message =>
            message.EventType == BillingEventTypes.WalletTopUpCompleted));
    }

    [Fact]
    public async Task Wallet_balance_drift_blocks_fulfillment_and_preserves_evidence()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        await SeedCorruptWalletBalanceAsync(provider, seeded.UserId);

        var result = await IngestAsync(
            provider,
            Transaction("b03-wallet-drift", seeded.OrderCode, 100_000, '5'));

        await AssertIntegrityConflictAsync(provider, seeded, result, 1, 0, 0);
    }

    [Fact]
    public async Task Paid_lot_drift_blocks_fulfillment_and_preserves_evidence()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        await SeedCorruptPaidLotAsync(provider, seeded.UserId);

        var result = await IngestAsync(
            provider,
            Transaction("b03-lot-drift", seeded.OrderCode, 100_000, '6'));

        await AssertIntegrityConflictAsync(provider, seeded, result, 0, 1, 0);
    }

    [Fact]
    public async Task Ledger_drift_blocks_fulfillment_and_preserves_evidence()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        await SeedCorruptLedgerAsync(provider, seeded.UserId);

        var result = await IngestAsync(
            provider,
            Transaction("b03-ledger-drift", seeded.OrderCode, 100_000, '7'));

        await AssertIntegrityConflictAsync(provider, seeded, result, 0, 0, 1);
    }

    [Fact]
    public async Task Orphan_success_outbox_blocks_fulfillment_and_prevents_a_second_event()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        await SeedSuccessOutboxAsync(provider, seeded);

        var result = await IngestAsync(
            provider,
            Transaction("b03-outbox-drift", seeded.OrderCode, 100_000, '0'));

        await AssertIntegrityConflictAsync(provider, seeded, result, 0, 0, 0, 1);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.OutboxMessages.CountAsync(message =>
            message.EventType == BillingEventTypes.WalletTopUpCompleted));
    }

    [Fact]
    public async Task Orphan_fulfilled_provider_state_blocks_fulfillment()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        await SeedFulfilledProviderTransactionAsync(provider, seeded);

        var result = await IngestAsync(
            provider,
            Transaction("b03-provider-drift", seeded.OrderCode, 100_000, 'f'));

        await AssertIntegrityConflictAsync(provider, seeded, result, 0, 0, 0);
    }

    [Fact]
    public async Task Existing_received_event_with_current_reconciliation_reason_is_persistently_reconciled()
    {
        await using var provider = await CreateProviderAsync();
        var normalized = Transaction(
            "b03-reconcile-upgrade",
            "ZENO-not-found",
            100_000,
            '8') with
        {
            ReconciliationReason = SePayReconciliationReasons.UnexpectedAccount,
        };
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedContext.ProviderTransactions.Add(ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                normalized with { ReconciliationReason = null },
                VerifiedAt.AddMinutes(-1)));
            await seedContext.SaveChangesAsync();
        }

        var result = await IngestAsync(provider, normalized);

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);
        Assert.True(result.IsReplay);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await context.ProviderTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(
            ProviderTransactionProcessingStatuses.ReconciliationRequired,
            persisted.ProcessingStatus);
        Assert.Equal(
            SePayReconciliationReasons.UnexpectedAccount,
            persisted.ReconciliationReason);
    }

    [Fact]
    public async Task TopUp_replay_waiting_on_fulfillment_observes_committed_success_without_concurrency_failure()
    {
        var observer = new BlockingFulfillmentObserver();
        await using var provider = await CreateProviderAsync(observer);
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var fulfillment = IngestAsync(
            provider,
            Transaction("b03-order-replay-race", seeded.OrderCode, 100_000, '9'));
        await observer.FirstLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var replay = ReplayTopUpOrderAsync(provider, seeded);
        await WaitForDatabaseLockWaiterAsync();
        Assert.False(replay.IsCompleted);
        observer.ReleaseFirst.TrySetResult();

        Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, (await fulfillment).Outcome);
        var replayResult = await replay;
        Assert.Equal(TopUpOrderOutcome.Succeeded, replayResult.Outcome);
        Assert.Equal(TopUpOrderStatuses.Succeeded, replayResult.Order?.OrderStatus);
    }

    [Fact]
    public async Task Pre_B03_received_transaction_is_fulfilled_on_exact_provider_replay()
    {
        await using var provider = await CreateProviderAsync();
        var seeded = await SeedOrderAsync(
            provider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var payment = Transaction(
            "b03-upgrade-replay",
            seeded.OrderCode,
            100_000,
            '3');
        Guid providerTransactionId;
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var received = ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                payment,
                DateTimeOffset.UtcNow.AddMinutes(-1));
            providerTransactionId = received.Id;
            context.ProviderTransactions.Add(received);
            await context.SaveChangesAsync();
        }

        var result = await IngestAsync(provider, payment);

        Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, result.Outcome);
        Assert.True(result.IsReplay);
        Assert.Equal(providerTransactionId, result.ProviderTransactionId);
        await using var scope = provider.CreateAsyncScope();
        var verification = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verification.PaidCoinLots.CountAsync(lot =>
            lot.TopUpOrderId == seeded.OrderId));
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_all_state_and_same_payment_can_retry()
    {
        await using var failingProvider = await CreateProviderAsync(
            new ThrowBeforeCommitObserver());
        var seeded = await SeedOrderAsync(
            failingProvider,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var payment = Transaction("b03-rollback", seeded.OrderCode, 100_000, '1');

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IngestAsync(failingProvider, payment));

        await using var retryProvider = await CreateProviderAsync();
        await using (var verificationScope = retryProvider.CreateAsyncScope())
        {
            var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await context.ProviderTransactions.AnyAsync(candidate =>
                candidate.ProviderTransactionId == payment.ProviderTransactionId));
            Assert.False(await context.PaidCoinLots.AnyAsync(candidate =>
                candidate.TopUpOrderId == seeded.OrderId));
            Assert.False(await context.WalletOperations.AnyAsync(candidate =>
                candidate.SourceId == seeded.OrderId));
            Assert.False(await context.WalletEntries.AnyAsync(candidate =>
                candidate.UserId == seeded.UserId));
            Assert.False(await context.OutboxMessages.AnyAsync(message =>
                message.EventType == BillingEventTypes.WalletTopUpCompleted));
            Assert.False(await context.WebhookDeliveries.AnyAsync());
            Assert.Equal(TopUpOrderStatuses.Pending, (await context.PaymentOrders
                .AsNoTracking()
                .SingleAsync(order => order.Id == seeded.OrderId)).OrderStatus);
        }

        var retry = await IngestAsync(retryProvider, payment);
        Assert.Equal(SePayWebhookStoreOutcome.Fulfilled, retry.Outcome);
    }

    private async Task<ServiceProvider> CreateProviderAsync(
        ISePayFulfillmentCriticalSectionObserver? observer = null,
        ISePayWebhookCriticalSectionObserver? webhookObserver = null)
    {
        var services = new ServiceCollection();
        services.AddPersistence(_postgres.GetConnectionString());
        if (observer is not null)
        {
            services.RemoveAll<ISePayFulfillmentCriticalSectionObserver>();
            services.AddSingleton(observer);
            services.AddSingleton<ISePayFulfillmentCriticalSectionObserver>(observer);
        }

        if (webhookObserver is not null)
        {
            services.RemoveAll<ISePayWebhookCriticalSectionObserver>();
            services.AddSingleton(webhookObserver);
            services.AddSingleton<ISePayWebhookCriticalSectionObserver>(webhookObserver);
        }

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Database.MigrateAsync();
        return provider;
    }

    private static async Task<SeededOrder> SeedOrderAsync(
        ServiceProvider provider,
        long amountVnd,
        DateTimeOffset expiresAt)
    {
        expiresAt = expiresAt.ToUniversalTime();
        var createdAt = expiresAt.AddMinutes(-30);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderCode = $"ZENO-{orderId:N}";
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            "B03 Fulfillment User",
            null,
            null,
            createdAt.AddMinutes(-1)));
        context.PaymentOrders.Add(PaymentOrder.CreateTopUp(
            orderId,
            userId,
            orderCode,
            amountVnd,
            TopUpAmountPolicy.CalculatePaidCoins(amountVnd),
            $"b03-{orderId:N}",
            new string('a', PaymentOrder.RequestFingerprintLength),
            createdAt,
            expiresAt));
        await context.SaveChangesAsync();
        return new SeededOrder(
            userId,
            orderId,
            orderCode,
            $"b03-{orderId:N}",
            new string('a', PaymentOrder.RequestFingerprintLength));
    }

    private static async Task<SePayWebhookStoreResult> IngestAsync(
        ServiceProvider provider,
        SePayNormalizedTransaction transaction)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISePayWebhookStore>()
            .IngestAsync(transaction, VerifiedAt);
    }

    private static async Task<TopUpOrderStoreResult> ReplayTopUpOrderAsync(
        ServiceProvider provider,
        SeededOrder seeded)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITopUpOrderStore>()
            .CreateOrReplayAsync(new CreateTopUpOrderStoreRequest(
                Guid.NewGuid(),
                seeded.UserId,
                100_000,
                10_000,
                seeded.IdempotencyKey,
                seeded.RequestFingerprint,
                TimeSpan.FromMinutes(30)));
    }

    private static SePayNormalizedTransaction Transaction(
        string providerTransactionId,
        string orderCode,
        long amountVnd,
        char fingerprintCharacter,
        DateTimeOffset? occurredAt = null) => new(
            providerTransactionId,
            new string(fingerprintCharacter, 64),
            amountVnd,
            ProviderTransactionDirections.Incoming,
            orderCode,
            "6789",
            $"REF-{providerTransactionId}",
            occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            null);

    private static async Task AssertNoFinancialCreditAsync(
        ServiceProvider provider,
        SeededOrder seeded,
        Guid providerTransactionId,
        string expectedReason,
        string expectedOrderStatus)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await context.PaymentOrders.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);
        Assert.Equal(expectedOrderStatus, order.OrderStatus);
        var providerTransaction = await context.ProviderTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == providerTransactionId);
        Assert.Equal(
            ProviderTransactionProcessingStatuses.ReconciliationRequired,
            providerTransaction.ProcessingStatus);
        Assert.Equal(expectedReason, providerTransaction.ReconciliationReason);
        Assert.Equal(seeded.OrderId, providerTransaction.MatchedOrderId);
        Assert.False(await context.PaidCoinLots.AnyAsync(lot =>
            lot.TopUpOrderId == seeded.OrderId));
        Assert.False(await context.WalletOperations.AnyAsync(operation =>
            operation.SourceId == seeded.OrderId));
        Assert.False(await context.OutboxMessages.AnyAsync(message =>
            message.EventType == BillingEventTypes.WalletTopUpCompleted));
        var wallet = await context.Wallets.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == seeded.UserId);
        Assert.True(wallet is null || wallet.PaidAvailable == 0);
    }

    private static async Task AssertIntegrityConflictAsync(
        ServiceProvider provider,
        SeededOrder seeded,
        SePayWebhookStoreResult result,
        long expectedPaidAvailable,
        int expectedLotCount,
        int expectedEntryCount,
        int expectedSuccessOutboxCount = 0)
    {
        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await context.PaymentOrders.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);
        var transaction = await context.ProviderTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == result.ProviderTransactionId);
        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == seeded.UserId);
        Assert.Equal(TopUpOrderStatuses.Pending, order.OrderStatus);
        Assert.Equal(
            ProviderTransactionProcessingStatuses.ReconciliationRequired,
            transaction.ProcessingStatus);
        Assert.Equal(
            SePayReconciliationReasons.FulfillmentStateConflict,
            transaction.ReconciliationReason);
        Assert.Equal(expectedPaidAvailable, wallet.PaidAvailable);
        Assert.Equal(expectedLotCount, await context.PaidCoinLots.CountAsync(lot =>
            lot.UserId == seeded.UserId));
        Assert.Equal(expectedEntryCount, await context.WalletEntries.CountAsync(entry =>
            entry.UserId == seeded.UserId));
        Assert.False(await context.PaidCoinLots.AnyAsync(lot =>
            lot.TopUpOrderId == seeded.OrderId));
        Assert.False(await context.WalletOperations.AnyAsync(operation =>
            operation.SourceId == seeded.OrderId));
        Assert.Equal(expectedSuccessOutboxCount, await context.OutboxMessages.CountAsync(message =>
            message.EventType == BillingEventTypes.WalletTopUpCompleted));
    }

    private static async Task SeedCorruptWalletBalanceAsync(
        ServiceProvider provider,
        Guid userId)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Wallets.Add(WalletAccount.Create(userId, VerifiedAt.AddMinutes(-2)));
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE wallet.wallets
            SET paid_available = 1
            WHERE user_id = {userId}
            """);
    }

    private static async Task SeedCorruptPaidLotAsync(
        ServiceProvider provider,
        Guid userId)
    {
        var orderId = Guid.NewGuid();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Wallets.Add(WalletAccount.Create(userId, VerifiedAt.AddMinutes(-2)));
        context.PaymentOrders.Add(PaymentOrder.CreateTopUp(
            orderId,
            userId,
            $"ZENO-{orderId:N}",
            10_000,
            1_000,
            $"corrupt-lot-{orderId:N}",
            new string('b', PaymentOrder.RequestFingerprintLength),
            VerifiedAt.AddMinutes(-2),
            VerifiedAt.AddMinutes(28)));
        context.PaidCoinLots.Add(PaidCoinLot.Create(
            Guid.NewGuid(),
            userId,
            orderId,
            1,
            VerifiedAt.AddMinutes(-1)));
        await context.SaveChangesAsync();
    }

    private static async Task SeedCorruptLedgerAsync(
        ServiceProvider provider,
        Guid userId)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Wallets.Add(WalletAccount.Create(userId, VerifiedAt.AddMinutes(-2)));
        var operation = WalletOperation.Create(
            Guid.NewGuid(),
            userId,
            "TestCredit",
            "IntegrityTest",
            Guid.NewGuid(),
            null,
            VerifiedAt.AddMinutes(-1));
        context.WalletOperations.Add(operation);
        context.WalletEntries.Add(WalletEntry.Create(
            Guid.NewGuid(),
            operation,
            WalletCoinTypes.Paid,
            WalletBuckets.Available,
            1,
            1,
            VerifiedAt.AddMinutes(-1)));
        await context.SaveChangesAsync();
    }

    private static async Task SeedSuccessOutboxAsync(
        ServiceProvider provider,
        SeededOrder seeded)
    {
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        writer.Enqueue(
            BillingEventTypes.WalletTopUpCompleted,
            new WalletTopUpCompletedEvent(
                seeded.OrderId,
                Guid.NewGuid(),
                seeded.UserId,
                100_000,
                10_000,
                VerifiedAt.AddMinutes(-1)),
            VerifiedAt.AddMinutes(-1));
        await context.SaveChangesAsync();
    }

    private static async Task SeedFulfilledProviderTransactionAsync(
        ServiceProvider provider,
        SeededOrder seeded)
    {
        var normalized = Transaction(
            "b03-existing-fulfilled",
            seeded.OrderCode,
            100_000,
            'e');
        var providerTransaction = ProviderTransaction.CreateSePay(
            Guid.NewGuid(),
            normalized,
            VerifiedAt.AddMinutes(-1));
        providerTransaction.MarkFulfilled(seeded.OrderId);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.ProviderTransactions.Add(providerTransaction);
        await context.SaveChangesAsync();
    }

    private async Task WaitForDatabaseLockWaiterAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND wait_event_type = 'Lock';
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

        throw new TimeoutException(
            "The competing TopUp fulfillment did not wait for a PostgreSQL lock.");
    }

    private sealed record SeededOrder(
        Guid UserId,
        Guid OrderId,
        string OrderCode,
        string IdempotencyKey,
        string RequestFingerprint);

    private sealed class BlockingFulfillmentObserver :
        ISePayFulfillmentCriticalSectionObserver
    {
        private int _entryCount;

        internal TaskCompletionSource FirstLockAcquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int EntryCount => Volatile.Read(ref _entryCount);

        public async Task AfterFinancialLocksAcquiredAsync(
            string providerTransactionId,
            Guid paymentOrderId,
            CancellationToken cancellationToken)
        {
            var entry = Interlocked.Increment(ref _entryCount);
            if (entry != 1)
            {
                return;
            }

            FirstLockAcquired.TrySetResult();
            await ReleaseFirst.Task.WaitAsync(cancellationToken);
        }

        public Task BeforeFulfillmentCommitAsync(
            string providerTransactionId,
            Guid paymentOrderId,
            CancellationToken cancellationToken) => Task.CompletedTask;
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
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "Injected failure before TopUp fulfillment commit.");
    }

    private sealed class BlockingWebhookObserver : ISePayWebhookCriticalSectionObserver
    {
        private int _entryCount;

        internal TaskCompletionSource FirstLockAcquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int EntryCount => Volatile.Read(ref _entryCount);

        public async Task AfterLockAcquiredAsync(
            string providerTransactionId,
            CancellationToken cancellationToken)
        {
            var entry = Interlocked.Increment(ref _entryCount);
            if (entry != 1)
            {
                return;
            }

            FirstLockAcquired.TrySetResult();
            await ReleaseFirst.Task.WaitAsync(cancellationToken);
        }
    }
}
