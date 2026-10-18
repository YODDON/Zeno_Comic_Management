using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using ComicManagement.Missions.Definitions;
using ComicManagement.Identity.Users;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class MissionMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261017191312_HardenChapterUnlockIntegrity";
    private const string MissionMigration =
        "20261018042934_AddMissionDefinitionsAndActionReceipts";
    private const string MissionHardeningMigration =
        "20261018124500_HardenMissionActivationConsistency";
    private const string MissionFingerprintMigration =
        "20261018155821_AddMissionReceiptPayloadFingerprint";

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

    [Fact]
    public async Task Hardening_migration_rejects_existing_drift_and_rolls_back_schema_change()
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
            "drifted-daily-login",
            "Drifted daily login",
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

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE missions.mission_definitions
            SET action_type = {MissionActionTypes.ComicFollowed}
            WHERE id = {definition.Id};
            """);

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            migrator.MigrateAsync(MissionHardeningMigration));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains("precondition failed", exception.MessageText, StringComparison.Ordinal);
        Assert.False(await MigrationApplied(context, MissionHardeningMigration));
        Assert.Equal(
            MissionActionTypes.ComicFollowed,
            await context.MissionDefinitions.AsNoTracking()
                .Where(candidate => candidate.Id == definition.Id)
                .Select(candidate => candidate.ActionType)
                .SingleAsync());

        var triggerDefinition = await context.Database.SqlQueryRaw<string>(
                "SELECT pg_get_triggerdef(oid) AS \"Value\" " +
                "FROM pg_trigger WHERE tgname = " +
                "'trg_mission_definition_active_revision_ownership'")
            .SingleAsync();
        Assert.DoesNotContain("action_type", triggerDefinition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fingerprint_migration_backfills_existing_receipt_without_data_loss()
    {
        await using var services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MissionHardeningMigration);

        var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        context.Users.Add(User.CreateFromGoogle(
            userId,
            "Legacy Mission Receipt",
            null,
            null,
            now));
        await context.SaveChangesAsync();
        var receiptId = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        const string sourceEventType = "Identity.UserLoggedIn.v1";
        const string processingStatus = "Pending";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO missions.action_receipts
                (id, source_event_id, source_event_type, user_id, subject_id,
                 occurred_at, occurred_local_date, received_at, processing_status)
            VALUES
                ({receiptId}, {sourceEventId}, {sourceEventType}, {userId}, NULL,
                 {now}, {new DateOnly(2026, 10, 18)}, {now}, {processingStatus});
            """);

        await migrator.MigrateAsync(MissionFingerprintMigration);

        var fingerprint = await context.Database.SqlQueryRaw<string>(
                "SELECT payload_fingerprint AS \"Value\" " +
                "FROM missions.action_receipts WHERE id = {0}",
                receiptId)
            .SingleAsync();
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.True(await MigrationApplied(context, MissionFingerprintMigration));
        Assert.Equal(
            1,
            await context.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::integer AS \"Value\" " +
                    "FROM missions.action_receipts WHERE id = {0}",
                    receiptId)
                .SingleAsync());
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
