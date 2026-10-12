using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class CatalogPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_catalog_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task PostgreSql_enforces_catalog_uniqueness_checks_foreign_keys_and_link_keys()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();

        var comicId = Guid.NewGuid();
        var genreId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var translationGroupId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);

        context.Comics.Add(Comic.Create(
            comicId,
            "catalog-constraints",
            "Catalog Constraints",
            "",
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            now));
        context.Genres.Add(Genre.Create(genreId, "action", "Action"));
        context.Authors.Add(Author.Create(authorId, "Author One"));
        context.TranslationGroups.Add(TranslationGroup.Create(translationGroupId, "Group One"));
        context.ComicGenres.Add(ComicGenre.Create(comicId, genreId));
        context.ComicAuthors.Add(ComicAuthor.Create(comicId, authorId));
        context.ComicTranslationGroups.Add(ComicTranslationGroup.Create(comicId, translationGroupId));
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Comics.CountAsync());
        Assert.Equal(1, await context.ComicGenres.CountAsync());
        Assert.Equal(1, await context.ComicAuthors.CountAsync());
        Assert.Equal(1, await context.ComicTranslationGroups.CountAsync());

        await AssertPostgresViolation(
            PostgresErrorCodes.UniqueViolation,
            () => InsertComic(context, Guid.NewGuid(), "catalog-constraints", 0, "Ongoing", "Draft", now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertComic(context, Guid.NewGuid(), "negative-price", -1, "Ongoing", "Draft", now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertComic(context, Guid.NewGuid(), "invalid-series", 0, "Cancelled", "Draft", now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertComic(context, Guid.NewGuid(), "invalid-publication", 0, "Ongoing", "Archived", now));
        await AssertPostgresViolation(
            PostgresErrorCodes.UniqueViolation,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO catalog.genres (id, slug, name) VALUES ({Guid.NewGuid()}, {"action"}, {"Other Action"});"));
        await AssertPostgresViolation(
            PostgresErrorCodes.UniqueViolation,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO catalog.comic_genres (comic_id, genre_id) VALUES ({comicId}, {genreId});"));
        await AssertPostgresViolation(
            PostgresErrorCodes.ForeignKeyViolation,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO catalog.comic_authors (comic_id, author_id) VALUES ({Guid.NewGuid()}, {authorId});"));

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM catalog.comics WHERE id = {comicId};");

        Assert.Equal(0, await context.ComicGenres.AsNoTracking().CountAsync());
        Assert.Equal(0, await context.ComicAuthors.AsNoTracking().CountAsync());
        Assert.Equal(0, await context.ComicTranslationGroups.AsNoTracking().CountAsync());
    }

    private static Task<int> InsertComic(
        AppDbContext context,
        Guid id,
        string slug,
        long price,
        string seriesStatus,
        string publicationStatus,
        DateTimeOffset now) => context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.comics
                (id, slug, title, description, series_status, publication_status,
                 first_published_at, cover_asset_id, default_price_coins,
                 default_vip_eligible, version, created_at, updated_at)
            VALUES
                ({id}, {slug}, {"Constraint Test"}, {""}, {seriesStatus}, {publicationStatus},
                 NULL, NULL, {price}, FALSE, 1, {now}, {now});
            """);

    private static async Task AssertPostgresViolation(string sqlState, Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(operation);
        Assert.Equal(sqlState, exception.SqlState);
    }
}
