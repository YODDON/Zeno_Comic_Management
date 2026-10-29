using ComicManagement.Billing.Payments;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class SePayWebhookPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset VerifiedAt =
        new(2026, 10, 16, 4, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task First_delivery_persists_normalized_transaction_and_delivery_atomically()
    {
        await using var provider = await CreateProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISePayWebhookStore>();

        var result = await store.IngestAsync(Transaction("1001", 'a'), VerifiedAt);

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, result.Outcome);
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transaction = await context.ProviderTransactions.AsNoTracking().SingleAsync();
        var delivery = await context.WebhookDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(result.ProviderTransactionId, transaction.Id);
        Assert.Equal("1001", transaction.ProviderTransactionId);
        Assert.Equal(
            ProviderTransactionProcessingStatuses.ReconciliationRequired,
            transaction.ProcessingStatus);
        Assert.Equal(
            SePayReconciliationReasons.OrderNotFound,
            transaction.ReconciliationReason);
        Assert.Equal(transaction.Id, delivery.ProviderTransactionId);
        Assert.Equal(VerifiedAt, transaction.VerifiedAt);
        Assert.Equal(VerifiedAt, delivery.ReceivedAt);
        Assert.True(delivery.AuthVerified);
        Assert.Equal(
            WebhookDeliveryProcessingResults.ReconciliationRequired,
            delivery.ProcessingResult);
        Assert.Empty(await context.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await context.PaidCoinLots.AsNoTracking().ToListAsync());
        Assert.Empty(await context.WalletOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await context.WalletEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.OutboxMessages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Same_transaction_and_payload_is_delivery_replay_without_duplicate_financial_event()
    {
        await using var provider = await CreateProviderAsync();
        var first = await IngestAsync(provider, Transaction("1002", 'b'));
        var replay = await IngestAsync(provider, Transaction("1002", 'b'));

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, first.Outcome);
        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, replay.Outcome);
        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.ProviderTransactionId, replay.ProviderTransactionId);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.ProviderTransactions.CountAsync());
        Assert.Equal(2, await context.WebhookDeliveries.CountAsync());
        Assert.Equal(
            1,
            await context.WebhookDeliveries.CountAsync(delivery =>
                delivery.ProcessingResult == WebhookDeliveryProcessingResults.Duplicate));
    }

    [Fact]
    public async Task Same_provider_identity_with_different_payload_requires_reconciliation()
    {
        await using var provider = await CreateProviderAsync();
        var first = await IngestAsync(provider, Transaction("1003", 'c'));
        var conflict = await IngestAsync(provider, Transaction("1003", 'd'));

        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, first.Outcome);
        Assert.Equal(SePayWebhookStoreOutcome.ReconciliationRequired, conflict.Outcome);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transaction = await context.ProviderTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(
            ProviderTransactionProcessingStatuses.ReconciliationRequired,
            transaction.ProcessingStatus);
        Assert.Equal(
            SePayReconciliationReasons.ProviderTransactionConflict,
            transaction.ReconciliationReason);
        var delivery = await context.WebhookDeliveries
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ErrorCode ==
                    SePayReconciliationReasons.ProviderTransactionConflict);
        Assert.Equal(
            WebhookDeliveryProcessingResults.ReconciliationRequired,
            delivery.ProcessingResult);
        Assert.Equal(
            SePayReconciliationReasons.ProviderTransactionConflict,
            delivery.ErrorCode);
    }

    [Fact]
    public async Task Concurrent_duplicate_really_contends_on_lock_and_creates_one_transaction()
    {
        var observer = new BlockingObserver();
        await using var provider = await CreateProviderAsync(observer);

        var firstTask = IngestAsync(provider, Transaction("1004", 'e'));
        await observer.FirstLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondTask = IngestAsync(provider, Transaction("1004", 'e'));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, observer.EntryCount);
        Assert.False(secondTask.IsCompleted);

        observer.ReleaseFirst.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(2, observer.EntryCount);
        Assert.All(results, result => Assert.Equal(
            SePayWebhookStoreOutcome.ReconciliationRequired,
            result.Outcome));
        Assert.Single(results, result => !result.IsReplay);
        Assert.Single(results, result => result.IsReplay);
        Assert.Single(results.Select(result => result.ProviderTransactionId).Distinct());

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.ProviderTransactions.CountAsync());
        Assert.Equal(2, await context.WebhookDeliveries.CountAsync());
    }

    [Fact]
    public async Task Database_constraints_reject_invalid_provider_financial_evidence()
    {
        await using var provider = await CreateProviderAsync();
        await IngestAsync(provider, Transaction("1005", 'f'));

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var invalidAmount = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "UPDATE billing.provider_transactions SET amount_vnd = 0;"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidAmount.SqlState);

        context.ChangeTracker.Clear();
        var invalidFulfillment = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                """
                UPDATE billing.provider_transactions
                SET processing_status = 'Fulfilled', reconciliation_reason = NULL;
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidFulfillment.SqlState);

        var unauthenticatedDelivery = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "UPDATE billing.webhook_deliveries SET auth_verified = FALSE;"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unauthenticatedDelivery.SqlState);
    }

    private async Task<ServiceProvider> CreateProviderAsync(
        ISePayWebhookCriticalSectionObserver? observer = null)
    {
        var services = new ServiceCollection();
        services.AddPersistence(_postgres.GetConnectionString());
        if (observer is not null)
        {
            services.RemoveAll<ISePayWebhookCriticalSectionObserver>();
            services.AddSingleton(observer);
            services.AddSingleton<ISePayWebhookCriticalSectionObserver>(observer);
        }

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .Database
            .MigrateAsync();
        return provider;
    }

    private static async Task<SePayWebhookStoreResult> IngestAsync(
        ServiceProvider provider,
        SePayNormalizedTransaction transaction)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<ISePayWebhookStore>()
            .IngestAsync(transaction, VerifiedAt);
    }

    private static SePayNormalizedTransaction Transaction(
        string providerTransactionId,
        char fingerprintCharacter) => new(
            providerTransactionId,
            new string(fingerprintCharacter, 64),
            100_000,
            ProviderTransactionDirections.Incoming,
            "ZENO-0123456789abcdef",
            "6789",
            "FT24012345678",
            new DateTimeOffset(2026, 10, 16, 3, 30, 0, TimeSpan.Zero),
            null);

    private sealed class BlockingObserver : ISePayWebhookCriticalSectionObserver
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
