using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class ChapterPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_chapter_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task PostgreSql_enforces_chapter_page_checks_uniqueness_foreign_keys_and_restricts_deletes()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();

        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

        context.Comics.Add(Comic.Create(
            comicId,
            "chapter-schema",
            "Chapter Schema",
            "",
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            now));
        context.Chapters.Add(Chapter.Create(chapterId, comicId, "Chapter 1", 1, null, null, now));
        context.ChapterPages.Add(ChapterPage.Create(
            pageId,
            chapterId,
            1,
            "comic-management/chapter/page-1",
            "Ready",
            1200,
            1800));
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Chapters.CountAsync());
        Assert.Equal(1, await context.ChapterPages.CountAsync());

        await AssertPostgresViolation(
            PostgresErrorCodes.UniqueViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, "Duplicate Order", 1, 0, "Draft", 1, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, "Negative Price", 2, -1, "Draft", 1, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, "Invalid Status", 2, null, "Archived", 1, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, " ", 2, null, "Draft", 1, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertChapter(context, Guid.NewGuid(), comicId, "Invalid Version", 2, null, "Draft", 0, now));
        await AssertPostgresViolation(
            PostgresErrorCodes.ForeignKeyViolation,
            () => InsertChapter(context, Guid.NewGuid(), Guid.NewGuid(), "Missing Comic", 1, null, "Draft", 1, now));

        await AssertPostgresViolation(
            PostgresErrorCodes.UniqueViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 1, "duplicate-order", "Ready", 100, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, " ", "Ready", 100, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, "page-2", " ", 100, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, "page-2", "Ready", 0, 100));
        await AssertPostgresViolation(
            PostgresErrorCodes.CheckViolation,
            () => InsertPage(context, Guid.NewGuid(), chapterId, 2, "page-2", "Ready", 100, 0));
        await AssertPostgresViolation(
            PostgresErrorCodes.ForeignKeyViolation,
            () => InsertPage(context, Guid.NewGuid(), Guid.NewGuid(), 1, "missing-chapter", "Ready", 100, 100));

        await AssertPostgresViolation(
            PostgresErrorCodes.RestrictViolation,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM catalog.chapters WHERE id = {chapterId};"));
        await AssertPostgresViolation(
            PostgresErrorCodes.RestrictViolation,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM catalog.comics WHERE id = {comicId};"));
    }

    private static Task<int> InsertChapter(
        AppDbContext context,
        Guid id,
        Guid comicId,
        string label,
        int sortOrder,
        long? priceOverride,
        string publicationStatus,
        long version,
        DateTimeOffset now) => context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.chapters
                (id, comic_id, chapter_label, sort_order, price_override_coins, vip_override,
                 publication_status, version, created_at, updated_at)
            VALUES
                ({id}, {comicId}, {label}, {sortOrder}, {priceOverride}, NULL,
                 {publicationStatus}, {version}, {now}, {now});
            """);

    private static Task<int> InsertPage(
        AppDbContext context,
        Guid id,
        Guid chapterId,
        int pageOrder,
        string publicId,
        string assetStatus,
        int width,
        int height) => context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.chapter_pages
                (id, chapter_id, page_order, cloudinary_public_id, asset_status, width, height)
            VALUES
                ({id}, {chapterId}, {pageOrder}, {publicId}, {assetStatus}, {width}, {height});
            """);

    private static async Task AssertPostgresViolation(string sqlState, Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(operation);
        Assert.Equal(sqlState, exception.SqlState);
    }
}
