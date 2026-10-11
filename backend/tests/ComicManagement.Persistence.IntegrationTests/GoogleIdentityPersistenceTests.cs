using ComicManagement.Identity.Authentication;
using ComicManagement.Persistence;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class GoogleIdentityPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_identity_tests")
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
    public async Task Concurrent_first_sign_ins_create_one_user_and_one_google_identity()
    {
        var profile = new GoogleUserProfile(
            "concurrent-google-subject",
            "Concurrent User",
            "concurrent@example.com",
            "https://example.com/avatar.png");

        var signIns = Enumerable.Range(0, 8)
            .Select(_ => SignInAsync(profile))
            .ToArray();
        var results = await Task.WhenAll(signIns);

        Assert.All(results, result =>
            Assert.Equal(GoogleSignInOutcome.Succeeded, result.Outcome));
        Assert.Single(results.Select(result => result.User!.Id).Distinct());

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(1, await context.Users.CountAsync());
        Assert.Equal(1, await context.GoogleIdentities.CountAsync());
        Assert.Equal(
            "concurrent-google-subject",
            await context.GoogleIdentities.Select(identity => identity.GoogleSubject).SingleAsync());
    }

    [Fact]
    public async Task Duplicate_subject_constraint_rejects_direct_duplicate_identity()
    {
        await SignInAsync(new GoogleUserProfile(
            "unique-subject",
            "First User",
            "first@example.com",
            null));

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identity.users
                (id, display_name, email, avatar_url, role, status, created_at, updated_at, version)
            VALUES
                (@user_id, 'Second User', NULL, NULL, 'User', 'Active', NOW(), NOW(), 1);
            INSERT INTO identity.google_identities (user_id, google_subject, linked_at)
            VALUES (@user_id, 'unique-subject', NOW());
            """;
        var userId = command.CreateParameter();
        userId.ParameterName = "user_id";
        userId.Value = Guid.NewGuid();
        command.Parameters.Add(userId);

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            async () => await command.ExecuteNonQueryAsync());

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("uq_google_identities_google_subject", exception.ConstraintName);
    }

    private async Task<GoogleSignInResult> SignInAsync(GoogleUserProfile profile)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = new GoogleSignInHandler(
            scope.ServiceProvider.GetRequiredService<IGoogleIdentityStore>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        return await handler.HandleAsync(profile, CancellationToken.None);
    }
}
