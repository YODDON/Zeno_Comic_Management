using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using ComicManagement.Missions.Definitions;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class MissionMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261017191312_HardenChapterUnlockIntegrity";
    private const string MissionMigration =
        "20261018042934_AddMissionDefinitionsAndActionReceipts";
    private const string MissionHardeningMigration =
        "20261018124500_HardenMissionActivationConsistency";

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
        await migrator.MigrateAsync(MissionHardeningMigration);

        Assert.True(await RelationExists(context, "missions.mission_definitions"));
        Assert.True(await RelationExists(context, "missions.mission_revisions"));
        Assert.True(await RelationExists(context, "missions.action_receipts"));
        Assert.True(await MigrationApplied(context, MissionMigration));
        Assert.True(await MigrationApplied(context, MissionHardeningMigration));
    }

    [Fact]
    public async Task Hardening_migration_preserves_active_mission_and_closes_action_type_drift()
    {
        await using var services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MissionMigration);

        var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var definition = MissionDefinition.Create(
            Guid.NewGuid(),
            "daily-login-upgrade",
            "Daily login upgrade",
            string.Empty,
            MissionActionTypes.DailyLogin,
            MissionCycleTypes.Daily,
            now);
        var revision = MissionRevision.Create(
            Guid.NewGuid(),
            definition.Id,
            1,
            1,
            10,
            MissionRepeatPolicies.OncePerVietnamDate,
            now,
            null,
            "{}");
        context.MissionDefinitions.Add(definition);
        context.MissionRevisions.Add(revision);
        await context.SaveChangesAsync();
        definition.Activate(revision, 1);
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(MissionHardeningMigration);
        context.ChangeTracker.Clear();

        Assert.Equal(
            revision.Id,
            (await context.MissionDefinitions.AsNoTracking().SingleAsync()).ActiveRevisionId);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE missions.mission_definitions
                SET action_type = {MissionActionTypes.ComicFollowed}
                WHERE id = {definition.Id};
                """));
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
