using ComicManagement.Billing.Payments;
using ComicManagement.Identity.Users;
using ComicManagement.Persistence;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private readonly MutableClock _clock = new(InitialNow);
    private ServiceProvider _services = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var services = new ServiceCollection();
        services.AddPersistence(_postgres.GetConnectionString());
        services.RemoveAll<IClock>();
        services.AddSingleton<IClock>(_clock);
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
        var results = await Task.WhenAll(
            CreateAsync(10_000, "same-key"),
            CreateAsync(10_000, "same-key"));

        Assert.All(results, result =>
            Assert.Equal(TopUpOrderOutcome.Succeeded, result.Outcome));
        Assert.Equal(results[0].State!.OrderId, results[1].State!.OrderId);
        Assert.Equal(1, await CountOrdersAsync());
    }

    [Fact]
    public async Task Concurrent_different_payloads_with_same_key_return_one_conflict()
    {
        var results = await Task.WhenAll(
            CreateAsync(10_000, "conflicting-key"),
            CreateAsync(20_000, "conflicting-key"));

        Assert.Single(results, result => result.Outcome == TopUpOrderOutcome.Succeeded);
        Assert.Single(results, result =>
            result.Outcome == TopUpOrderOutcome.IdempotencyConflict);
        Assert.Equal(1, await CountOrdersAsync());
    }

    [Fact]
    public async Task Expired_order_replays_without_resetting_idempotency()
    {
        var created = await CreateAsync(29_000, "expiry-key");
        _clock.UtcNow = InitialNow.AddMinutes(30);

        var replay = await CreateAsync(29_000, "expiry-key");

        Assert.Equal(created.State!.OrderId, replay.State!.OrderId);
        Assert.Equal(TopUpOrderStatuses.Expired, replay.State.Status);

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

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
