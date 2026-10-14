using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
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
        "20261014195842_AddReadingProgressAcceptedMaximum",
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
        Assert.True(await TableExists(context, "reading.comic_follows"));
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

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        var migratedPage = await context.ChapterPages.SingleAsync(page => page.Id == pageId);
        Assert.Equal(ChapterPageAssetStatuses.Pending, migratedPage.AssetStatus);
        Assert.StartsWith("legacy:", migratedPage.UploadFingerprint, StringComparison.Ordinal);
        var migratedChapter = await context.Chapters.SingleAsync(
            chapter => chapter.Id == legacyPublishedChapterId);
        Assert.Equal(now, migratedChapter.PublishedAt);

        await context.Database.MigrateAsync();

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

        await context.Database.MigrateAsync();

        Assert.Equal(DatabaseSchemas.All.Order(), await GetApplicationSchemas(context));
        Assert.Equal(ExpectedMigrations, await context.Database.GetAppliedMigrationsAsync());
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
}
