using ComicManagement.Catalog.Browsing;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using ComicManagement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class PublicCatalogQueryPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_public_catalog_tests")
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
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Public_list_and_detail_return_only_published_valid_catalog_data()
    {
        var publicComicId = Guid.NewGuid();
        var draftComicId = Guid.NewGuid();
        var genreId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var publicComic = Comic.Create(
                publicComicId,
                "public-hero",
                "The 100% Public Hero",
                "Published detail",
                ComicSeriesStatuses.Ongoing,
                "covers/public-hero",
                25,
                true,
                Now);
            AddPublishedChapter(context, publicComic, 1, 0);
            publicComic.Publish(true, 1, Now.AddMinutes(1));

            var draftComic = Comic.Create(
                draftComicId,
                "draft-hero",
                "Draft Hero",
                "Private detail",
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now);
            AddPublishedChapter(context, draftComic, 1, 0);

            context.Comics.AddRange(publicComic, draftComic);
            context.Genres.Add(Genre.Create(genreId, "action", "Action"));
            context.Authors.Add(Author.Create(authorId, "Author One"));
            context.TranslationGroups.Add(TranslationGroup.Create(groupId, "Group One"));
            context.ComicGenres.Add(ComicGenre.Create(publicComicId, genreId));
            context.ComicAuthors.Add(ComicAuthor.Create(publicComicId, authorId));
            context.ComicTranslationGroups.Add(
                ComicTranslationGroup.Create(publicComicId, groupId));
            await context.SaveChangesAsync();
        }

        await using var queryScope = _services.CreateAsyncScope();
        var handler = queryScope.ServiceProvider.GetRequiredService<PublicCatalogHandler>();

        var list = await handler.ListComicsAsync(
            "% Public",
            "action",
            ComicSeriesStatuses.Ongoing,
            null,
            null);
        var item = Assert.Single(list.Page!.Items);
        Assert.Equal(publicComicId, item.Id);
        Assert.Equal("Action", Assert.Single(item.Genres).Name);

        var detail = await handler.GetComicAsync("public-hero");
        Assert.Equal(PublicCatalogOutcome.Succeeded, detail.Outcome);
        Assert.Equal("Published detail", detail.Detail!.Description);
        Assert.Equal("Author One", Assert.Single(detail.Detail.Authors).Name);
        Assert.Equal("Group One", Assert.Single(detail.Detail.TranslationGroups).Name);

        Assert.Equal(
            PublicCatalogOutcome.NotFound,
            (await handler.GetComicAsync("draft-hero")).Outcome);
        Assert.Equal(
            PublicCatalogOutcome.NotFound,
            (await handler.ListChaptersAsync(draftComicId, null)).Outcome);
    }

    [Fact]
    public async Task Chapter_pagination_returns_fixed_thirty_items_total_metadata_and_access_summary()
    {
        var comicId = Guid.NewGuid();
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                "sixty-one-chapters",
                "Sixty-one Chapters",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                25,
                true,
                Now);
            for (var sortOrder = 1; sortOrder <= 61; sortOrder++)
            {
                long? priceOverride = sortOrder switch
                {
                    1 => 0,
                    3 => 40,
                    _ => null,
                };
                AddPublishedChapter(context, comic, sortOrder, priceOverride);
            }

            comic.Publish(true, 1, Now.AddMinutes(1));
            context.Comics.Add(comic);
            context.Chapters.Add(Chapter.Create(
                Guid.NewGuid(),
                comicId,
                "Draft chapter",
                62,
                0,
                false,
                Now));
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO catalog.chapters
                    (id, comic_id, chapter_label, sort_order, price_override_coins,
                     vip_override, publication_status, published_at, version, created_at, updated_at)
                VALUES
                    ({Guid.NewGuid()}, {comicId}, {"Invalid published chapter"}, {1000}, {0L},
                     {false}, {ChapterPublicationStatuses.Published}, {Now}, {1L}, {Now}, {Now});
                """);
        }

        await using var queryScope = _services.CreateAsyncScope();
        var handler = queryScope.ServiceProvider.GetRequiredService<PublicCatalogHandler>();

        var first = await handler.ListChaptersAsync(comicId, 1);
        var second = await handler.ListChaptersAsync(comicId, 2);
        var third = await handler.ListChaptersAsync(comicId, 3);
        var firstPage = Assert.IsType<PublicChapterListPage>(first.Page);
        var secondPage = Assert.IsType<PublicChapterListPage>(second.Page);
        var thirdPage = Assert.IsType<PublicChapterListPage>(third.Page);

        Assert.Equal(30, firstPage.Items.Count);
        Assert.Equal(30, secondPage.Items.Count);
        Assert.Single(thirdPage.Items);
        Assert.Equal(1, firstPage.Page);
        Assert.Equal(2, secondPage.Page);
        Assert.Equal(3, thirdPage.Page);
        Assert.All([firstPage, secondPage, thirdPage], page =>
        {
            Assert.Equal(30, page.PageSize);
            Assert.Equal(61, page.TotalItems);
            Assert.Equal(3, page.TotalPages);
        });
        Assert.Equal(Enumerable.Range(1, 61),
            firstPage.Items
                .Concat(secondPage.Items)
                .Concat(thirdPage.Items)
                .Select(chapter => chapter.SortOrder));

        Assert.Equal("Free", firstPage.Items[0].AccessSummary.AccessType);
        Assert.True(firstPage.Items[0].AccessSummary.CanRead);
        Assert.Equal(0, firstPage.Items[0].AccessSummary.FinalPriceCoins);
        Assert.Equal("Locked", firstPage.Items[1].AccessSummary.AccessType);
        Assert.False(firstPage.Items[1].AccessSummary.CanRead);
        Assert.Equal(25, firstPage.Items[1].AccessSummary.FinalPriceCoins);
        Assert.Equal(40, firstPage.Items[2].AccessSummary.FinalPriceCoins);
    }

    [Fact]
    public async Task Comic_keyset_pagination_is_stable_when_update_sort_timestamps_are_equal()
    {
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var index = 1; index <= 31; index++)
            {
                var comic = Comic.Create(
                    Guid.NewGuid(),
                    $"equal-time-{index}",
                    $"Equal Time {index}",
                    string.Empty,
                    ComicSeriesStatuses.Completed,
                    null,
                    0,
                    false,
                    Now);
                AddPublishedChapter(context, comic, 1, 0);
                comic.Publish(true, 1, Now.AddMinutes(1));
                context.Comics.Add(comic);
            }

            await context.SaveChangesAsync();
        }

        await using var queryScope = _services.CreateAsyncScope();
        var handler = queryScope.ServiceProvider.GetRequiredService<PublicCatalogHandler>();
        var first = await handler.ListComicsAsync(null, null, null, null, null);
        var second = await handler.ListComicsAsync(
            null,
            null,
            null,
            first.Page!.NextCursor,
            null);

        Assert.Equal(30, first.Page.Items.Count);
        Assert.Single(second.Page!.Items);
        Assert.Equal(
            31,
            first.Page.Items.Concat(second.Page.Items).Select(comic => comic.Id).Distinct().Count());
        Assert.Null(second.Page.NextCursor);
    }

    [Fact]
    public async Task Comic_update_sort_uses_latest_valid_published_chapter_not_comic_updated_at()
    {
        var olderContentComicId = Guid.NewGuid();
        var newerContentComicId = Guid.NewGuid();
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var olderContent = Comic.Create(
                olderContentComicId,
                "older-content",
                "Older Content",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now);
            AddPublishedChapter(
                context,
                olderContent,
                1,
                0,
                Now.AddMinutes(1));
            olderContent.Publish(true, 1, Now.AddMinutes(2));

            var newerContent = Comic.Create(
                newerContentComicId,
                "newer-content",
                "Newer Content",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now);
            AddPublishedChapter(
                context,
                newerContent,
                1,
                0,
                Now.AddMinutes(10));
            newerContent.Publish(true, 1, Now.AddMinutes(11));

            context.Comics.AddRange(olderContent, newerContent);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE catalog.comics SET updated_at = {Now.AddDays(1)} WHERE id = {olderContentComicId};");
        }

        await using var queryScope = _services.CreateAsyncScope();
        var handler = queryScope.ServiceProvider.GetRequiredService<PublicCatalogHandler>();

        var result = await handler.ListComicsAsync(null, null, null, null, null);

        Assert.Equal(
            [newerContentComicId, olderContentComicId],
            result.Page!.Items.Select(comic => comic.Id));
        Assert.Equal(Now.AddMinutes(10), result.Page.Items[0].UpdateSortAt);
        Assert.Equal(Now.AddMinutes(1), result.Page.Items[1].UpdateSortAt);
    }

    private static void AddPublishedChapter(
        AppDbContext context,
        Comic comic,
        int sortOrder,
        long? priceOverride,
        DateTimeOffset? publishedAt = null)
    {
        var chapter = Chapter.Create(
            Guid.NewGuid(),
            comic.Id,
            $"Chapter {sortOrder}",
            sortOrder,
            priceOverride,
            null,
            Now);
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapter.Id,
            1,
            $"public-catalog/{comic.Id:N}/{sortOrder}",
            $"sha256:{sortOrder:x64}",
            Now);
        page.MarkReady(1200, 1800);
        chapter.Publish(true, 1, publishedAt ?? Now.AddSeconds(sortOrder));
        context.Chapters.Add(chapter);
        context.ChapterPages.Add(page);
    }
}
