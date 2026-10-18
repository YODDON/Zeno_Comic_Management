using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class MissionMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261017191312_HardenChapterUnlockIntegrity";
    private const string MissionMigration =
        "20261018042934_AddMissionDefinitionsAndActionReceipts";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_mission_migration_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Failed_migration_rolls_back_all_mission_schema_changes()
    {
        await using var services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE missions.action_receipts (id uuid PRIMARY KEY);");

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            migrator.MigrateAsync(MissionMigration));

        Assert.False(await RelationExists(context, "missions.mission_definitions"));
        Assert.False(await RelationExists(context, "missions.mission_revisions"));
        Assert.False(await MigrationApplied(context, MissionMigration));

        await context.Database.ExecuteSqlRawAsync(
            "DROP TABLE missions.action_receipts;");
        await migrator.MigrateAsync(MissionMigration);

        Assert.True(await RelationExists(context, "missions.mission_definitions"));
        Assert.True(await RelationExists(context, "missions.mission_revisions"));
        Assert.True(await RelationExists(context, "missions.action_receipts"));
        Assert.True(await MigrationApplied(context, MissionMigration));
    }

    private static async Task<bool> RelationExists(
        AppDbContext context,
        string relation) => await context.Database
        .SqlQueryRaw<bool>("SELECT to_regclass({0}) IS NOT NULL AS \"Value\"", relation)
        .SingleAsync();

    private static async Task<bool> MigrationApplied(
        AppDbContext context,
        string migration) => await context.Database
        .SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM infrastructure.__ef_migrations_history " +
            "WHERE \"MigrationId\" = {0}) AS \"Value\"",
            migration)
        .SingleAsync();
}
