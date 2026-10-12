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
}
