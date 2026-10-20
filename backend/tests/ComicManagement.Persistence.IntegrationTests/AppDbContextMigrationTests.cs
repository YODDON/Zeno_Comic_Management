using ComicManagement.Billing.Payments;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Membership.Plans;
using ComicManagement.Persistence;
using ComicManagement.Identity.Users;
using ComicManagement.Wallet.Lots;
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
    private const string PreV01Migration = "20261016073318_AddTopUpFulfillment";
    private const string V01Migration = "20261016172119_AddVipMembership";
    private const string U01Migration = "20261017082258_AddChapterUnlockQuotes";
    private const string U02Migration = "20261017130827_AddAtomicChapterUnlocks";
    private const string U02HardeningMigration =
        "20261017191312_HardenChapterUnlockIntegrity";
    private const string M01Migration =
        "20261018042934_AddMissionDefinitionsAndActionReceipts";
    private const string M01HardeningMigration =
        "20261018124500_HardenMissionActivationConsistency";
    private const string M01ReceiptFingerprintMigration =
        "20261018155821_AddMissionReceiptPayloadFingerprint";
    private const string M02Migration =
        "20261018164734_AddMissionProgressAndRewards";
    private const string M02WorkerIndexMigration =
        "20261018164955_AddMissionReceiptWorkerIndex";
    private const string RF01Migration =
        "20261019082250_AddRefundRequests";
    private const string RF01SecurityMigration =
        "20261019150424_HardenRefundRequestSecurity";

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
        "20261016073318_AddTopUpFulfillment",
        "20261016172119_AddVipMembership",
        U01Migration,
        U02Migration,
        U02HardeningMigration,
        M01Migration,
        M01HardeningMigration,
        M01ReceiptFingerprintMigration,
        M02Migration,
        M02WorkerIndexMigration,
        RF01Migration,
        RF01SecurityMigration,
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
        Assert.True(await TableExists(context, "reading.chapter_unlock_quotes"));
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
        Assert.True(await TableExists(context, "billing.refund_requests"));
        Assert.True(await TableExists(context, "billing.refund_recipients"));
        Assert.True(await TableExists(context, "membership.vip_plans"));
        Assert.True(await TableExists(context, "membership.vip_subscriptions"));
        Assert.True(await TableExists(context, "membership.vip_transactions"));
        Assert.True(await TableExists(context, "missions.mission_definitions"));
        Assert.True(await TableExists(context, "missions.mission_revisions"));
        Assert.True(await TableExists(context, "missions.action_receipts"));
        Assert.True(await TableExists(context, "missions.mission_activations"));
        Assert.True(await TableExists(context, "missions.mission_progress"));
        Assert.True(await TableExists(context, "missions.progress_actions"));
        Assert.True(await TableExists(context, "missions.reward_grants"));
        Assert.True(await TableExists(context, "missions.daily_counters"));
        Assert.True(await IndexExists(
            context,
            "missions",
            "ux_mission_activations_mission_sequence"));
        Assert.True(await IndexExists(
            context,
            "missions",
            "ux_mission_activations_open"));
        Assert.True(await IndexExists(
            context,
            "missions",
            "ix_action_receipts_pending_worker"));
        Assert.True(await IndexExists(
            context,
            "missions",
            "ux_mission_progress_user_revision_period"));
        Assert.True(await IndexExists(
            context,
            "missions",
            "ux_reward_grants_wallet_operation"));
        Assert.True(await TriggerExists(
            context,
            "missions",
            "mission_activations",
            "mission_activations_history_guard"));
        Assert.True(await TriggerExists(
            context,
            "missions",
            "reward_grants",
            "reward_grants_financial_integrity"));
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
        Assert.Equal(
            29_000L,
            await context.Database.SqlQueryRaw<long>(
                    "SELECT price_vnd AS \"Value\" FROM membership.vip_plans WHERE is_active")
                .SingleAsync());
        Assert.True(await IndexExists(
            context,
            "billing",
            "ux_payment_orders_user_operation_idempotency"));
        Assert.True(await IndexExists(
            context,
            "billing",
            "ux_refund_requests_payment_order_id"));
        Assert.True(await IndexExists(
            context,
            "billing",
            "ux_refund_requests_user_idempotency"));
        Assert.False(await IndexExists(
            context,
            "billing",
            "IX_refund_requests_payment_order_id_user_id_payment_order_type"));
        Assert.False(await IndexExists(
            context,
            "billing",
            "IX_refund_requests_payment_order_id_user_id_payment_order_type~"));
        Assert.True(await IndexExists(context, "wallet", "ux_wallet_allocations_operation_lot"));
        Assert.True(await TriggerExists(
            context,
            "wallet",
            "wallet_allocations",
            "wallet_allocations_enforce_ownership"));
        Assert.True(await TriggerExists(
            context,
            "membership",
            "vip_transactions",
            "vip_transactions_append_only"));

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
    public async Task RF01_incremental_migration_preserves_existing_fulfilled_topup_and_paid_lot()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(M02WorkerIndexMigration);

        var paidAt = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        context.Users.Add(User.CreateFromGoogle(
            userId, "RF01 Upgrade User", null, null, paidAt.AddDays(-1)));
        var order = PaymentOrder.CreateTopUp(
            orderId,
            userId,
            $"ZENO-{orderId:N}",
            100_000,
            10_000,
            $"rf01-upgrade-{orderId:N}",
            new string('a', 64),
            paidAt.AddMinutes(-10),
            paidAt.AddMinutes(10));
        order.FulfillTopUp(paidAt);
        context.PaymentOrders.Add(order);
        context.PaidCoinLots.Add(PaidCoinLot.Create(
            Guid.NewGuid(), userId, orderId, 10_000, paidAt));
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(RF01SecurityMigration);

        Assert.True(await TableExists(context, "billing.refund_requests"));
        Assert.True(await TableExists(context, "billing.refund_recipients"));
        Assert.Equal(1, await context.PaymentOrders.CountAsync(item => item.Id == orderId));
        Assert.Equal(1, await context.PaidCoinLots.CountAsync(item => item.TopUpOrderId == orderId));
        Assert.Empty(await context.RefundRequests.ToListAsync());
    }

    [Fact]
    public async Task RF01_security_migration_rejects_legacy_unkeyed_fingerprints_and_rolls_back()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(RF01Migration);

        var paidAt = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        context.Users.Add(User.CreateFromGoogle(
            userId, "RF01 Legacy Fingerprint", null, null, paidAt.AddDays(-1)));
        var order = PaymentOrder.CreateTopUp(
            orderId,
            userId,
            $"ZENO-{orderId:N}",
            100_000,
            10_000,
            $"rf01-legacy-{orderId:N}",
            new string('a', 64),
            paidAt.AddMinutes(-10),
            paidAt.AddMinutes(10));
        order.FulfillTopUp(paidAt);
        context.PaymentOrders.Add(order);
        context.PaidCoinLots.Add(PaidCoinLot.Create(
            Guid.NewGuid(), userId, orderId, 10_000, paidAt));
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO billing.refund_requests
                (id, payment_order_id, user_id, payment_order_type, reason,
                 original_amount_vnd, fee_amount_vnd, net_refund_vnd,
                 required_recovery_coins, status, idempotency_key,
                 request_fingerprint, requested_at, version)
            VALUES
                ({refundId}, {orderId}, {userId}, {PaymentOrderTypes.TopUp}, {"legacy"},
                 {100_000L}, {5_000L}, {95_000L}, {10_000L}, {"Requested"},
                 {"legacy-key"}, {new string('a', 64)}, {paidAt}, {1L})
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => migrator.MigrateAsync(RF01SecurityMigration));
        Assert.Contains("RF01_SECURITY_PRECONDITION", exception.MessageText,
            StringComparison.Ordinal);
        Assert.False(await ColumnExists(
            context,
            "billing",
            "refund_requests",
            "request_fingerprint_version"));
        Assert.Equal(
            1,
            await context.Database.SqlQueryRaw<int>(
                "SELECT count(*)::integer AS \"Value\" FROM billing.refund_requests")
                .SingleAsync());
        Assert.DoesNotContain(
            RF01SecurityMigration,
            await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task B01_retains_unmatched_legacy_lot_but_B03_blocks_until_reconciled()
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

        await migrator.MigrateAsync("20261015185709_AddSePayWebhookIngestion");

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

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            context.Database.MigrateAsync());
        Assert.Equal("P0001", exception.SqlState);
        Assert.Contains(
            "reconcile legacy lots",
            exception.MessageText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task B03_upgrade_backfills_and_validates_owned_topup_lots()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261015185709_AddSePayWebhookIngestion");

        var now = new DateTimeOffset(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "B03 Upgrade User",
            null,
            null,
            now));
        context.PaymentOrders.Add(PaymentOrder.CreateTopUp(
            orderId,
            userId,
            $"ZENO-{orderId:N}",
            10_000,
            1_000,
            "b03-upgrade",
            new string('a', PaymentOrder.RequestFingerprintLength),
            now,
            now.AddMinutes(30)));
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO wallet.paid_coin_lots
                (id, user_id, topup_order_id, granted_coins, spent_coins,
                 refunded_coins, reserved_coins, created_at, version)
            VALUES
                ({lotId}, {userId}, {orderId}, {1_000L}, {0L},
                 {0L}, {0L}, {now}, {1L});
            """);

        await context.Database.MigrateAsync();

        Assert.Equal(
            PaymentOrderTypes.TopUp,
            await context.Database.SqlQueryRaw<string>(
                    "SELECT topup_order_type AS \"Value\" " +
                    "FROM wallet.paid_coin_lots WHERE id = {0}",
                    lotId)
                .SingleAsync());
        Assert.True(await ConstraintIsValidated(
            context,
            "wallet",
            "paid_coin_lots",
            "fk_paid_coin_lots_topup_payment_orders"));
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

    [Fact]
    public async Task V01_upgrade_preserves_existing_B03_topup_state()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreV01Migration);

        var now = new DateTimeOffset(2026, 10, 16, 17, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "V01 Upgrade User",
            null,
            null,
            now));
        context.PaymentOrders.Add(PaymentOrder.CreateTopUp(
            orderId,
            userId,
            "TOPUP-V01-UPGRADE",
            100_000,
            10_000,
            "v01-upgrade-key",
            new string('b', PaymentOrder.RequestFingerprintLength),
            now,
            now.AddMinutes(30)));
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        var preserved = await context.PaymentOrders
            .AsNoTracking()
            .SingleAsync(order => order.Id == orderId);
        Assert.Equal(PaymentOrderTypes.TopUp, preserved.OrderType);
        Assert.Equal(100_000, preserved.AmountVnd);
        Assert.Equal(10_000, preserved.PaidCoinsSnapshot);
        Assert.Equal(
            29_000,
            await context.Database.SqlQueryRaw<long>(
                    "SELECT price_vnd AS \"Value\" " +
                    "FROM membership.vip_plans WHERE is_active")
                .SingleAsync());
    }

    [Fact]
    public async Task V01_upgrade_preserves_compatible_legacy_vip_purchase_and_links_seeded_plan()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreV01Migration);

        var now = new DateTimeOffset(2026, 10, 16, 18, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "V01 Legacy Compatible",
            null,
            null,
            now));
        context.PaymentOrders.Add(PaymentOrder.CreateVipPurchase(
            orderId,
            userId,
            $"ZENO-VIP-{orderId:N}",
            29_000,
            VipPlan.InitialPlanId,
            VipPlan.FixedDurationHours,
            VipPlan.FixedDiscountPercent,
            "v01-legacy-compatible",
            new string('c', PaymentOrder.RequestFingerprintLength),
            now,
            now.AddMinutes(30)));
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        var preserved = await context.PaymentOrders.AsNoTracking().SingleAsync(candidate =>
            candidate.Id == orderId);
        Assert.Equal(PaymentOrderTypes.VipPurchase, preserved.OrderType);
        Assert.Equal(VipPlan.InitialPlanId, preserved.VipPlanId);
        Assert.Equal(VipPlan.FixedDurationHours, preserved.VipDurationHoursSnapshot);
        Assert.Equal(VipPlan.FixedDiscountPercent, preserved.VipDiscountPercentSnapshot);
        Assert.Equal(
            1,
            await context.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" " +
                    "FROM membership.vip_plans AS plan " +
                    "JOIN billing.payment_orders AS payment_order " +
                    "ON payment_order.vip_plan_id = plan.id " +
                    "WHERE payment_order.id = {0}",
                    orderId)
                .SingleAsync());
        Assert.True(await ConstraintIsValidated(
            context,
            "billing",
            "payment_orders",
            "fk_payment_orders_vip_plans"));
    }

    [Fact]
    public async Task V01_upgrade_rejects_fulfilled_legacy_vip_purchase_and_rolls_back()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreV01Migration);

        var createdAt = new DateTimeOffset(2026, 10, 16, 18, 30, 0, TimeSpan.Zero);
        var fulfilledAt = createdAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "V01 Legacy Fulfilled",
            null,
            null,
            createdAt));
        var order = PaymentOrder.CreateVipPurchase(
            orderId,
            userId,
            $"ZENO-VIP-{orderId:N}",
            29_000,
            VipPlan.InitialPlanId,
            VipPlan.FixedDurationHours,
            VipPlan.FixedDiscountPercent,
            "v01-legacy-fulfilled",
            new string('e', PaymentOrder.RequestFingerprintLength),
            createdAt,
            createdAt.AddMinutes(30));
        order.FulfillVipPurchase(fulfilledAt);
        context.PaymentOrders.Add(order);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.MigrateAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains(
            "fulfilled legacy VipPurchase requires reconciliation",
            exception.MessageText,
            StringComparison.Ordinal);

        await using var verificationProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var verificationScope = verificationProvider.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await TableExists(verification, "membership.vip_plans"));
        Assert.Equal(
            TopUpOrderStatuses.Succeeded,
            await verification.Database.SqlQueryRaw<string>(
                    "SELECT order_status AS \"Value\" " +
                    "FROM billing.payment_orders WHERE id = {0}",
                    orderId)
                .SingleAsync());
        Assert.False(await verification.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM infrastructure.__ef_migrations_history " +
                $"WHERE \"MigrationId\" = '{V01Migration}') AS \"Value\"")
            .SingleAsync());
    }

    [Fact]
    public async Task V01_upgrade_rejects_incompatible_legacy_vip_purchase_and_rolls_back()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreV01Migration);

        var now = new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderCode = $"ZENO-VIP-{orderId:N}";
        var legacyPlanId = Guid.NewGuid();
        var orderType = PaymentOrderTypes.VipPurchase;
        var providerName = PaymentProviders.SePay;
        var providerStatus = ProviderPaymentStatuses.Pending;
        var fulfillmentStatus = PaymentFulfillmentStatuses.NotStarted;
        var orderStatus = TopUpOrderStatuses.Pending;
        const string idempotencyKey = "v01-legacy-incompatible";
        var requestFingerprint = new string('d', PaymentOrder.RequestFingerprintLength);
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "V01 Legacy Incompatible",
            null,
            null,
            now));
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO billing.payment_orders
                (id, user_id, order_code, order_type, amount_vnd, paid_coins_snapshot,
                 vip_plan_id, vip_duration_hours_snapshot, vip_discount_percent_snapshot,
                 provider, provider_payment_status, fulfillment_status, idempotency_key,
                 request_fingerprint, order_status, expires_at, expired_at,
                 verified_paid_at, fulfilled_at, created_at, version)
            VALUES
                ({orderId}, {userId}, {orderCode}, {orderType}, {29_000L}, NULL,
                 {legacyPlanId}, {24}, {10}, {providerName}, {providerStatus}, {fulfillmentStatus},
                 {idempotencyKey}, {requestFingerprint}, {orderStatus},
                 {now.AddMinutes(30)}, NULL, NULL, NULL, {now}, {1L});
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.MigrateAsync());
        Assert.Equal("23514", exception.SqlState);
        Assert.Contains(
            "legacy VipPurchase snapshots require reconciliation",
            exception.MessageText,
            StringComparison.Ordinal);

        await using var verificationProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var verificationScope = verificationProvider.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await TableExists(verification, "membership.vip_plans"));
        Assert.Equal(
            24,
            await verification.Database.SqlQueryRaw<int>(
                    "SELECT vip_duration_hours_snapshot AS \"Value\" " +
                    "FROM billing.payment_orders WHERE id = {0}",
                    orderId)
                .SingleAsync());
        Assert.False(await verification.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM infrastructure.__ef_migrations_history " +
                $"WHERE \"MigrationId\" = '{V01Migration}') AS \"Value\"")
            .SingleAsync());
    }

    [Fact]
    public async Task U01_upgrade_preserves_existing_catalog_and_identity_data()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(V01Migration);

        var now = new DateTimeOffset(2026, 10, 17, 7, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "U01 Upgrade User",
            null,
            null,
            now));
        var comic = Comic.Create(
            comicId,
            $"u01-upgrade-{comicId:N}",
            "U01 Upgrade",
            string.Empty,
            ComicSeriesStatuses.Ongoing,
            null,
            100,
            false,
            now);
        var chapter = Chapter.Create(
            chapterId,
            comicId,
            "Chapter 1",
            1,
            null,
            null,
            now);
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            1,
            $"u01-upgrade/{chapterId:N}/1",
            $"sha256:{new string('a', 64)}",
            now);
        page.MarkReady(1200, 1800);
        chapter.Publish(true, 1, now.AddMinutes(1));
        comic.Publish(true, 1, now.AddMinutes(2));
        context.Comics.Add(comic);
        context.Chapters.Add(chapter);
        context.ChapterPages.Add(page);
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(U01Migration);
        context.ChangeTracker.Clear();

        Assert.True(await context.Users.AnyAsync(candidate => candidate.Id == userId));
        Assert.True(await context.Comics.AnyAsync(candidate => candidate.Id == comicId));
        Assert.True(await context.Chapters.AnyAsync(candidate => candidate.Id == chapterId));
        Assert.Empty(await context.ChapterUnlockQuotes.ToArrayAsync());
        foreach (var constraint in new[]
                 {
                     "ck_chapter_unlock_quotes_price",
                     "ck_chapter_unlock_quotes_estimate",
                     "ck_chapter_unlock_quotes_expiry",
                     "ck_chapter_unlock_quotes_revision",
                     "fk_chapter_unlock_quotes_users",
                     "fk_chapter_unlock_quotes_chapters",
                 })
        {
            Assert.True(await ConstraintIsValidated(
                context,
                "reading",
                "chapter_unlock_quotes",
                constraint));
        }

        Assert.Equal(
            ["user_id", "expires_at"],
            await IndexColumns(
                context,
                "reading",
                "ix_chapter_unlock_quotes_user_expiry"));
        Assert.Equal(
            ["chapter_id", "expires_at"],
            await IndexColumns(
                context,
                "reading",
                "ix_chapter_unlock_quotes_chapter_expiry"));
        Assert.True(await TriggerExists(
            context,
            "reading",
            "chapter_unlock_quotes",
            "chapter_unlock_quotes_lifecycle"));
    }

    [Fact]
    public async Task U01_migration_failure_rolls_back_without_partial_schema_or_data_loss()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(V01Migration);

        var userId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId,
            "U01 Rollback User",
            null,
            null,
            new DateTimeOffset(2026, 10, 17, 7, 30, 0, TimeSpan.Zero)));
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION reading.enforce_chapter_unlock_quote_lifecycle()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                RETURN OLD;
            END;
            $function$;
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync(U01Migration));
        Assert.Equal("42723", exception.SqlState);

        Assert.False(await TableExists(context, "reading.chapter_unlock_quotes"));
        Assert.True(await context.Users.AsNoTracking().AnyAsync(user => user.Id == userId));
        Assert.False(await context.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM infrastructure.__ef_migrations_history " +
                $"WHERE \"MigrationId\" = '{U01Migration}') AS \"Value\"")
            .SingleAsync());

        await context.Database.ExecuteSqlRawAsync(
            "DROP FUNCTION reading.enforce_chapter_unlock_quote_lifecycle();");
        await migrator.MigrateAsync(U01Migration);
        Assert.True(await TableExists(context, "reading.chapter_unlock_quotes"));
    }

    [Fact]
    public async Task U02_upgrade_preserves_existing_quotes_and_adds_entitlement_constraints()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(U01Migration);

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId, "U02 Upgrade User", null, null, now));
        var comic = Comic.Create(
            comicId, $"u02-upgrade-{comicId:N}", "U02 Upgrade", string.Empty,
            ComicSeriesStatuses.Ongoing, null, 80, false, now);
        var chapter = Chapter.Create(
            chapterId, comicId, "Chapter 1", 1, null, null, now);
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(), chapterId, 1, $"u02/{chapterId:N}/1",
            $"sha256:{new string('c', 64)}", now);
        page.MarkReady(1200, 1800);
        chapter.Publish(true, 1, now.AddSeconds(1));
        comic.Publish(true, 1, now.AddSeconds(2));
        context.Comics.Add(comic);
        context.Chapters.Add(chapter);
        context.ChapterPages.Add(page);
        var quote = ComicManagement.Reading.Unlocks.ChapterUnlockQuote.Create(
            Guid.NewGuid(), userId, chapterId, 80, 0, 0, 80, now);
        context.ChapterUnlockQuotes.Add(quote);
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(U02HardeningMigration);
        context.ChangeTracker.Clear();

        Assert.True(await context.ChapterUnlockQuotes.AsNoTracking()
            .AnyAsync(value => value.Id == quote.Id));
        Assert.True(await TableExists(context, "reading.chapter_unlocks"));
        Assert.True(await ConstraintIsValidated(
            context,
            "reading",
            "chapter_unlocks",
            "ck_chapter_unlocks_spend"));
        Assert.True(await TriggerExists(
            context,
            "reading",
            "chapter_unlocks",
            "trg_chapter_unlocks_append_only"));
        Assert.True(await TriggerExists(
            context,
            "reading",
            "chapter_unlocks",
            "trg_chapter_unlock_financial_integrity"));
        Assert.True(await IndexExists(
            context,
            "reading",
            "ux_chapter_unlocks_user_chapter"));
        Assert.True(await IndexExists(
            context,
            "reading",
            "ux_chapter_unlocks_quote"));
        Assert.True(await IndexExists(
            context,
            "reading",
            "ux_chapter_unlocks_wallet_operation"));
        Assert.True(await IndexExists(
            context,
            "reading",
            "ix_chapter_unlocks_chapter"));
        Assert.False(await IndexExists(
            context,
            "reading",
            "IX_chapter_unlocks_quote_id_user_id_chapter_id"));
        Assert.False(await IndexExists(
            context,
            "reading",
            "IX_chapter_unlocks_wallet_operation_id_user_id"));
        Assert.Equal(
            4,
            await context.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::integer AS \"Value\" FROM pg_constraint " +
                    "WHERE conrelid = 'reading.chapter_unlocks'::regclass " +
                    "AND contype = 'f'")
                .SingleAsync());
    }

    [Fact]
    public async Task U02_migration_failure_rolls_back_without_partial_quote_schema_change()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(U01Migration);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE reading.chapter_unlocks (id uuid PRIMARY KEY);");

        await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync(U02Migration));

        Assert.False(await context.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_constraint " +
                "WHERE conname = 'ak_chapter_unlock_quotes_id_user_chapter') AS \"Value\"")
            .SingleAsync());
        Assert.False(await context.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM infrastructure.__ef_migrations_history " +
                $"WHERE \"MigrationId\" = '{U02Migration}') AS \"Value\"")
            .SingleAsync());

        await context.Database.ExecuteSqlRawAsync("DROP TABLE reading.chapter_unlocks;");
        await migrator.MigrateAsync(U02Migration);
        Assert.True(await TableExists(context, "reading.chapter_unlocks"));
    }

    [Fact]
    public async Task U02_hardening_rejects_legacy_invalid_financial_link_and_rolls_back()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(U02Migration);

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        context.Users.Add(ComicManagement.Identity.Users.User.CreateFromGoogle(
            userId, "Invalid Legacy Unlock", null, null, now));
        var comic = Comic.Create(
            comicId, $"invalid-u02-{comicId:N}", "Invalid U02", string.Empty,
            ComicSeriesStatuses.Ongoing, null, 80, false, now);
        var chapter = Chapter.Create(
            chapterId, comicId, "Chapter 1", 1, null, null, now);
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(), chapterId, 1, $"invalid-u02/{chapterId:N}/1",
            $"sha256:{new string('d', 64)}", now);
        page.MarkReady(1200, 1800);
        chapter.Publish(true, 1, now.AddSeconds(1));
        comic.Publish(true, 1, now.AddSeconds(2));
        var quote = ComicManagement.Reading.Unlocks.ChapterUnlockQuote.Create(
            Guid.NewGuid(), userId, chapterId, 80, 0, 0, 80, now);
        context.Comics.Add(comic);
        context.Chapters.Add(chapter);
        context.ChapterPages.Add(page);
        context.ChapterUnlockQuotes.Add(quote);
        context.WalletOperations.Add(ComicManagement.Wallet.Ledger.WalletOperation.Create(
            operationId,
            userId,
            ComicManagement.Wallet.Ledger.WalletOperationTypes.TopUpCredit,
            ComicManagement.Wallet.Ledger.WalletOperationSourceTypes.PaymentOrder,
            Guid.NewGuid(),
            $"invalid-u02-{Guid.NewGuid():N}",
            now));
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reading.chapter_unlocks
                (id,user_id,chapter_id,quote_id,wallet_operation_id,
                 price_paid_coins,bonus_spent_coins,paid_spent_coins,
                 wallet_paid_available_after,wallet_bonus_available_after,unlocked_at)
            VALUES ({Guid.NewGuid()},{userId},{chapterId},{quote.Id},{operationId},
                    {80L},{80L},{0L},{0L},{0L},{now})
            """);

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync(U02HardeningMigration));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.True(await IndexExists(
            context,
            "reading",
            "IX_chapter_unlocks_quote_id_user_id_chapter_id"));
        Assert.False(await TriggerExists(
            context,
            "reading",
            "chapter_unlocks",
            "trg_chapter_unlock_financial_integrity"));
        Assert.False(await context.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM infrastructure.__ef_migrations_history " +
                $"WHERE \"MigrationId\" = '{U02HardeningMigration}') AS \"Value\"")
            .SingleAsync());
        Assert.True(await context.ChapterUnlocks.AsNoTracking().AnyAsync());
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

    private static async Task<string[]> IndexColumns(
        AppDbContext context,
        string schemaName,
        string indexName)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT attribute.attname
            FROM pg_class index_class
            JOIN pg_namespace index_namespace
              ON index_namespace.oid = index_class.relnamespace
            JOIN pg_index index_metadata
              ON index_metadata.indexrelid = index_class.oid
            JOIN unnest(index_metadata.indkey) WITH ORDINALITY
              AS key_columns(attribute_number, position)
              ON TRUE
            JOIN pg_attribute attribute
              ON attribute.attrelid = index_metadata.indrelid
             AND attribute.attnum = key_columns.attribute_number
            WHERE index_namespace.nspname = @schema_name
              AND index_class.relname = @index_name
            ORDER BY key_columns.position;
            """;
        foreach (var (name, value) in new[]
                 {
                     ("schema_name", schemaName),
                     ("index_name", indexName),
                 })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns.ToArray();
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
