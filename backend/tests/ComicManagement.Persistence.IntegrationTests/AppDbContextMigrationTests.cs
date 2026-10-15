using ComicManagement.Billing.Payments;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class AppDbContextMigrationTests : IAsyncLifetime
{
    private const string InitialMigration = "20261011120000_InitialSchemas";

    private static readonly string[] ExpectedMigrations =
    [
        InitialMigration,
        "20261011124604_AddOutboxInbox",
        "20261011194443_AddIdentityUsers",
        "20261012112702_AddCatalogTaxonomyAndComics",
        "20261012151245_AddCatalogChaptersAndPages",
        "20261012161522_DefineChapterPageAssetLifecycle",
        "20261012171656_AddChapterPageUploadFingerprint",
        "20261013044212_AddAdministrationAuditLogs",
        "20261013052440_AddPublicCatalogQueryIndexes",
        "20261013060258_AddChapterPublishedAtAndCatalogPagination",
        "20261013185204_AddReadingSessionsAndProgress",
        "20261014043925_AddComicFollows",
        "20261014094117_AddWalletCore",
        "20261014160437_AddWalletSpending",
        "20261014195842_AddReadingProgressAcceptedMaximum",
        "20261015141141_AddPaymentOrders",
        "20261015145756_HardenB01PaymentOrderInvariants",
        "20261015185709_AddSePayWebhookIngestion",
    ];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrations_create_all_schemas_and_support_reapply()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(InitialMigration);

        Assert.Equal([InitialMigration], await context.Database.GetAppliedMigrationsAsync());
        Assert.False(await TableExists(context, "infrastructure.outbox_messages"));
        Assert.False(await TableExists(context, "infrastructure.processed_messages"));

        await context.Database.MigrateAsync();

        Assert.Equal(DatabaseSchemas.All.Order(), await GetApplicationSchemas(context));
        Assert.Equal(ExpectedMigrations, await context.Database.GetAppliedMigrationsAsync());
        Assert.True(await TableExists(context, "infrastructure.outbox_messages"));
        Assert.True(await TableExists(context, "infrastructure.processed_messages"));
        Assert.True(await TableExists(context, "identity.users"));
        Assert.True(await TableExists(context, "identity.google_identities"));
        Assert.True(await TableExists(context, "catalog.comics"));
        Assert.True(await TableExists(context, "catalog.genres"));
        Assert.True(await TableExists(context, "catalog.authors"));
        Assert.True(await TableExists(context, "catalog.translation_groups"));
        Assert.True(await TableExists(context, "catalog.comic_genres"));
        Assert.True(await TableExists(context, "catalog.comic_authors"));
        Assert.True(await TableExists(context, "catalog.comic_translation_groups"));
        Assert.True(await TableExists(context, "catalog.chapters"));
        Assert.True(await TableExists(context, "catalog.chapter_pages"));
        Assert.True(await TableExists(context, "administration.audit_logs"));
        Assert.True(await TableExists(context, "reading.reading_sessions"));
        Assert.True(await TableExists(context, "reading.reading_progress"));
        Assert.True(await TableExists(context, "wallet.wallets"));
        Assert.True(await TableExists(context, "wallet.paid_coin_lots"));
        Assert.True(await TableExists(context, "wallet.wallet_operations"));
        Assert.True(await TableExists(context, "wallet.wallet_entries"));
        Assert.True(await TableExists(context, "wallet.wallet_allocations"));
        Assert.True(await TableExists(context, "reading.comic_follows"));
        Assert.True(await TableExists(context, "billing.payment_orders"));
        Assert.True(await TableExists(context, "billing.provider_transactions"));
        Assert.True(await TableExists(context, "billing.webhook_deliveries"));
        Assert.True(await IndexExists(context, "catalog", "ix_comics_publication_updated_id"));
        Assert.True(await IndexExists(context, "catalog", "ix_chapters_public_comic_sort_id"));
        Assert.True(await IndexExists(
            context,
            "catalog",
            "ix_chapters_public_comic_published_id"));
        Assert.True(await ColumnExists(context, "catalog", "chapters", "published_at"));
        Assert.True(await IndexExists(context, "wallet", "ix_paid_coin_lots_user_created_id"));
        Assert.True(await IndexExists(context, "wallet", "ix_wallet_entries_user_created_id"));
        Assert.True(await IndexExists(context, "wallet", "ux_wallet_operations_business_source"));
        Assert.True(await IndexExists(context, "billing", "ux_payment_orders_order_code"));
        Assert.True(await IndexExists(
            context,
            "billing",
            "ux_payment_orders_user_operation_idempotency"));
        Assert.True(await IndexExists(context, "wallet", "ux_wallet_allocations_operation_lot"));
        Assert.True(await TriggerExists(
            context,
            "wallet",
            "wallet_allocations",
            "wallet_allocations_enforce_ownership"));

        var now = new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        context.Comics.Add(Comic.Create(
            comicId,
            "migration-pending-page",
            "Migration Pending Page",
            string.Empty,
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            now));
        context.Chapters.Add(Chapter.Create(
            chapterId,
            comicId,
            "Chapter 1",
            1,
            null,
            null,
            now));
        context.ChapterPages.Add(ChapterPage.CreatePending(
            pageId,
            chapterId,
            1,
            "comic-management/chapter-pages/migration/pending",
            $"sha256:{new string('a', 64)}",
            now));
        await context.SaveChangesAsync();

        await migrator.MigrateAsync("20261012151245_AddCatalogChaptersAndPages");
        Assert.Equal(1, await CountRows(context, "catalog.chapter_pages"));
        var legacyPublishedChapterId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.chapters
                (id, comic_id, chapter_label, sort_order, price_override_coins,
                 vip_override, publication_status, version, created_at, updated_at)
            VALUES
                ({legacyPublishedChapterId}, {comicId}, {"Legacy published"}, {2}, NULL,
                 NULL, {ChapterPublicationStatuses.Published}, {1L}, {now}, {now});
            """);

        const string PreR03Migration = "20261014094117_AddWalletCore";
        await migrator.MigrateAsync(PreR03Migration);
        context.ChangeTracker.Clear();
        var migratedPage = await context.ChapterPages.SingleAsync(page => page.Id == pageId);
        Assert.Equal(ChapterPageAssetStatuses.Pending, migratedPage.AssetStatus);
        Assert.StartsWith("legacy:", migratedPage.UploadFingerprint, StringComparison.Ordinal);
        var migratedChapter = await context.Chapters.SingleAsync(
            chapter => chapter.Id == legacyPublishedChapterId);
        Assert.Equal(now, migratedChapter.PublishedAt);

        var legacyUserId = Guid.NewGuid();
        var legacySessionId = Guid.NewGuid();
        const int legacyAcceptedMaximum = 8_750;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO identity.users
                (id, display_name, email, avatar_url, role, status,
                 created_at, updated_at, version)
            VALUES
                ({legacyUserId}, {"R02 Migration User"}, NULL, NULL, {"User"}, {"Active"},
                 {now}, {now}, {1L});

            INSERT INTO reading.reading_sessions
                (id, user_id, comic_id, chapter_id, started_at, expires_at,
                 last_activity_at, last_activity_was_ready, active_duration_ms,
                 current_page_order, current_page_offset_ratio,
                 max_progress_basis_points, manifest_revision, manifest_page_count,
                 last_accepted_sequence, last_request_fingerprint,
                 minimum_progress_basis_points, minimum_active_duration_ms,
                 policy_revision, qualified_at, status, version)
            VALUES
                ({legacySessionId}, {legacyUserId}, {comicId}, {chapterId}, {now},
                 {now.AddMinutes(30)}, {now.AddSeconds(15)}, TRUE, {15_000L},
                 {1}, {0.75m}, {legacyAcceptedMaximum}, {new string('a', 64)}, {1},
                 {1L}, {new string('b', 64)}, {9_000}, {15_000L}, {"r02-v1"},
                 NULL, {"Active"}, {2L});

            INSERT INTO reading.reading_progress
                (user_id, comic_id, last_chapter_id, last_page_order,
                 last_page_offset_ratio, last_read_at, source_session_id,
                 source_session_started_at, version)
            VALUES
                ({legacyUserId}, {comicId}, {chapterId}, {1}, {0.75m},
                 {now.AddSeconds(15)}, {legacySessionId}, {now}, {1L});
            """);

        await context.Database.MigrateAsync();

        var preservedAcceptedMaximum = await context.Database.SqlQueryRaw<int>(
                "SELECT accepted_max_progress_basis_points AS \"Value\" " +
                "FROM reading.reading_progress")
            .SingleAsync();
        Assert.Equal(legacyAcceptedMaximum, preservedAcceptedMaximum);

        Assert.Equal(ExpectedMigrations, await context.Database.GetAppliedMigrationsAsync());

        await migrator.MigrateAsync(Migration.InitialDatabase);

        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal([DatabaseSchemas.Infrastructure], await GetApplicationSchemas(context));
        Assert.False(await TableExists(context, "infrastructure.outbox_messages"));
        Assert.False(await TableExists(context, "infrastructure.processed_messages"));
        Assert.False(await TableExists(context, "identity.users"));
        Assert.False(await TableExists(context, "identity.google_identities"));
        Assert.False(await TableExists(context, "catalog.comics"));
        Assert.False(await TableExists(context, "catalog.genres"));
        Assert.False(await TableExists(context, "catalog.chapters"));
        Assert.False(await TableExists(context, "catalog.chapter_pages"));
        Assert.False(await TableExists(context, "administration.audit_logs"));
        Assert.False(await TableExists(context, "reading.reading_sessions"));
        Assert.False(await TableExists(context, "reading.reading_progress"));
        Assert.False(await TableExists(context, "wallet.wallets"));
        Assert.False(await TableExists(context, "wallet.paid_coin_lots"));
        Assert.False(await TableExists(context, "wallet.wallet_operations"));
        Assert.False(await TableExists(context, "wallet.wallet_entries"));
        Assert.False(await TableExists(context, "billing.payment_orders"));
        Assert.False(await TableExists(context, "wallet.wallet_allocations"));

        await context.Database.MigrateAsync();

        Assert.Equal(DatabaseSchemas.All.Order(), await GetApplicationSchemas(context));
        Assert.Equal(ExpectedMigrations, await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task B01_upgrade_preserves_valid_W01_paid_lots_and_enforces_new_ownership_links()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261014094117_AddWalletCore");

        var now = new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var legacyOrderId = Guid.NewGuid();
        var legacyLotId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "Legacy Wallet User",
            null,
            null,
            now));
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO wallet.paid_coin_lots
                (id, user_id, topup_order_id, granted_coins, spent_coins,
                 refunded_coins, reserved_coins, created_at, version)
            VALUES
                ({legacyLotId}, {userId}, {legacyOrderId}, {1_000L}, {0L},
                 {0L}, {0L}, {now}, {1L});
            """);

        await context.Database.MigrateAsync();

        Assert.Equal(
            1,
            await context.Database.SqlQueryRaw<int>(
                    "SELECT 1 AS \"Value\" FROM wallet.paid_coin_lots WHERE id = {0}",
                    legacyLotId)
                .SingleAsync());
        Assert.False(await ConstraintIsValidated(
            context,
            "wallet",
            "paid_coin_lots",
            "fk_paid_coin_lots_topup_payment_orders"));

        var newLotId = Guid.NewGuid();
        var newOrderId = Guid.NewGuid();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO wallet.paid_coin_lots
                    (id, user_id, topup_order_id, granted_coins, spent_coins,
                     refunded_coins, reserved_coins, created_at, version)
                VALUES
                    ({newLotId}, {userId}, {newOrderId}, {1_000L}, {0L},
                     {0L}, {0L}, {now}, {1L});
                """));
    }

    [Fact]
    public async Task B02_upgrade_preserves_existing_B01_payment_orders()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261015145756_HardenB01PaymentOrderInvariants");

        var now = new DateTimeOffset(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "B01 Upgrade User",
            null,
            null,
            now));
        context.PaymentOrders.Add(PaymentOrder.CreateTopUp(
            orderId,
            userId,
            "TOPUP-B01-UPGRADE",
            100_000,
            10_000,
            "b01-upgrade-key",
            new string('a', 64),
            now,
            now.AddMinutes(30)));
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        Assert.True(await context.PaymentOrders.AnyAsync(order => order.Id == orderId));
        Assert.True(await TableExists(context, "billing.provider_transactions"));
        Assert.True(await TableExists(context, "billing.webhook_deliveries"));
    }

    private static async Task<string[]> GetApplicationSchemas(AppDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT schema_name
            FROM information_schema.schemata
            WHERE schema_name NOT LIKE 'pg_%'
              AND schema_name NOT IN ('information_schema', 'public')
            ORDER BY schema_name;
            """;

        var schemas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            schemas.Add(reader.GetString(0));
        }

        return [.. schemas];
    }

    private static async Task<bool> TableExists(AppDbContext context, string qualifiedTableName)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@qualified_table_name) IS NOT NULL;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "qualified_table_name";
        parameter.Value = qualifiedTableName;
        command.Parameters.Add(parameter);

        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Table existence query returned null."));
    }

    private static async Task<bool> ConstraintIsValidated(
        AppDbContext context,
        string schema,
        string table,
        string constraint)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pg_constraint.convalidated
            FROM pg_constraint
            JOIN pg_class ON pg_class.oid = pg_constraint.conrelid
            JOIN pg_namespace ON pg_namespace.oid = pg_class.relnamespace
            WHERE pg_namespace.nspname = @schema
              AND pg_class.relname = @table
              AND pg_constraint.conname = @constraint;
            """;
        foreach (var (name, value) in new[]
                 {
                     ("schema", schema),
                     ("table", table),
                     ("constraint", constraint),
                 })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<bool> ColumnExists(
        AppDbContext context,
        string schemaName,
        string tableName,
        string columnName)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            "WHERE table_schema = @schema_name AND table_name = @table_name " +
            "AND column_name = @column_name);";
        foreach (var (name, value) in new[]
                 {
                     ("schema_name", schemaName),
                     ("table_name", tableName),
                     ("column_name", columnName),
                 })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Column existence query returned null."));
    }

    private static async Task<int> CountRows(AppDbContext context, string qualifiedTableName)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {qualifiedTableName};";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> IndexExists(
        AppDbContext context,
        string schemaName,
        string indexName)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM pg_indexes " +
            "WHERE schemaname = @schema_name AND indexname = @index_name);";
        var schemaParameter = command.CreateParameter();
        schemaParameter.ParameterName = "schema_name";
        schemaParameter.Value = schemaName;
        command.Parameters.Add(schemaParameter);
        var indexParameter = command.CreateParameter();
        indexParameter.ParameterName = "index_name";
        indexParameter.Value = indexName;
        command.Parameters.Add(indexParameter);

        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Index existence query returned null."));
    }

    private static async Task<bool> TriggerExists(
        AppDbContext context,
        string schemaName,
        string tableName,
        string triggerName)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.triggers " +
            "WHERE event_object_schema = @schema_name AND event_object_table = @table_name " +
            "AND trigger_name = @trigger_name);";
        foreach (var (name, value) in new[]
                 {
                     ("schema_name", schemaName),
                     ("table_name", tableName),
                     ("trigger_name", triggerName),
                 })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Trigger existence query returned null."));
    }
}
