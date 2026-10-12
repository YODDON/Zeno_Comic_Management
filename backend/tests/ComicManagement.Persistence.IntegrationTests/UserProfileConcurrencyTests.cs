using ComicManagement.Identity.Profiles;
using ComicManagement.Identity.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class UserProfileConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_profile_race_tests")
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
        await scope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .Database
            .MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_profile_updates_have_exactly_one_winner_in_postgresql()
    {
        var userId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

        await using (var seedScope = _services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Users.Add(User.CreateFromGoogle(
                userId,
                "Original name",
                "profile-race@example.com",
                null,
                createdAt));
            await context.SaveChangesAsync();
        }

        await using var firstScope = _services.CreateAsyncScope();
        await using var secondScope = _services.CreateAsyncScope();
        var firstStore = firstScope.ServiceProvider.GetRequiredService<IUserProfileStore>();
        var secondStore = secondScope.ServiceProvider.GetRequiredService<IUserProfileStore>();
        var firstUser = Assert.IsType<User>(
            await firstStore.FindByIdAsync(userId, CancellationToken.None));
        var secondUser = Assert.IsType<User>(
            await secondStore.FindByIdAsync(userId, CancellationToken.None));

        Assert.Equal(1, firstUser.Version);
        Assert.Equal(1, secondUser.Version);
        Assert.True(firstUser.UpdateDisplayName("First contender", createdAt.AddMinutes(1)));
        Assert.True(secondUser.UpdateDisplayName("Second contender", createdAt.AddMinutes(2)));

        var outcomes = await Task.WhenAll(
            firstStore.SaveChangesAsync(CancellationToken.None),
            secondStore.SaveChangesAsync(CancellationToken.None));

        Assert.Single(outcomes, outcome => outcome == UserProfileSaveOutcome.Succeeded);
        Assert.Single(outcomes, outcome => outcome == UserProfileSaveOutcome.ConcurrencyConflict);

        await using var verificationScope = _services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == userId);
        Assert.Contains(persisted.DisplayName, new[] { "First contender", "Second contender" });
        Assert.Equal(2, persisted.Version);
    }
}
