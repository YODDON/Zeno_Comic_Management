using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using Xunit;

namespace ComicManagement.Catalog.UnitTests;

public sealed class CatalogModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_comic_starts_as_draft_with_optimistic_concurrency_version()
    {
        var comic = Comic.Create(
            Guid.NewGuid(),
            "the-final-hero",
            "The Final Hero",
            "",
            ComicSeriesStatuses.Ongoing,
            null,
            25,
            true,
            Now);

        Assert.Equal(ComicPublicationStatuses.Draft, comic.PublicationStatus);
        Assert.Null(comic.FirstPublishedAt);
        Assert.Equal(1, comic.Version);
        Assert.Equal(Now, comic.CreatedAt);
        Assert.Equal(Now, comic.UpdatedAt);
    }

    [Theory]
    [InlineData(ComicSeriesStatuses.Ongoing)]
    [InlineData(ComicSeriesStatuses.Completed)]
    [InlineData(ComicSeriesStatuses.Hiatus)]
    public void Comic_accepts_each_approved_series_status(string seriesStatus)
    {
        var comic = CreateComic(seriesStatus: seriesStatus);

        Assert.Equal(seriesStatus, comic.SeriesStatus);
    }

    [Fact]
    public void Comic_rejects_invalid_identity_slug_status_price_and_timestamp()
    {
        Assert.Throws<ArgumentException>(() => CreateComic(id: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateComic(slug: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateComic(slug: new string('a', Comic.MaximumSlugLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateComic(seriesStatus: "Cancelled"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateComic(defaultPriceCoins: -1));
        Assert.Throws<ArgumentException>(() => CreateComic(createdAt: Now.ToOffset(TimeSpan.FromHours(7))));
    }

    [Fact]
    public void Comic_publish_requires_valid_chapter_and_locks_slug_forever()
    {
        var comic = CreateComic();

        Assert.Throws<CatalogRuleViolationException>(() =>
            comic.Publish(false, 1, Now.AddMinutes(1)));

        comic.ChangeSlug("renamed-before-publish", 1, Now.AddMinutes(1));
        comic.Publish(true, 2, Now.AddMinutes(2));
        var firstPublishedAt = comic.FirstPublishedAt;

        Assert.Equal(ComicPublicationStatuses.Published, comic.PublicationStatus);
        Assert.Equal(Now.AddMinutes(2), firstPublishedAt);
        Assert.Equal(3, comic.Version);

        comic.Hide(3, Now.AddMinutes(3));
        comic.Publish(true, 4, Now.AddMinutes(4));

        Assert.Equal(firstPublishedAt, comic.FirstPublishedAt);
        var exception = Assert.Throws<CatalogRuleViolationException>(() =>
            comic.ChangeSlug("forbidden-after-publish", 5, Now.AddMinutes(5)));
        Assert.Equal(CatalogRuleViolation.SlugLocked, exception.Violation);
    }

    [Fact]
    public void Comic_publication_commands_reject_stale_versions()
    {
        var comic = CreateComic();

        var exception = Assert.Throws<CatalogRuleViolationException>(() =>
            comic.Publish(true, 2, Now.AddMinutes(1)));

        Assert.Equal(CatalogRuleViolation.ConcurrencyConflict, exception.Violation);
        Assert.Equal(ComicPublicationStatuses.Draft, comic.PublicationStatus);
    }

    [Fact]
    public void Taxonomy_and_links_reject_missing_required_values()
    {
        Assert.Throws<ArgumentException>(() => Genre.Create(Guid.Empty, "action", "Action"));
        Assert.Throws<ArgumentException>(() => Genre.Create(Guid.NewGuid(), " ", "Action"));
        Assert.Throws<ArgumentException>(() => Author.Create(Guid.NewGuid(), " "));
        Assert.Throws<ArgumentException>(() => TranslationGroup.Create(Guid.NewGuid(), " "));
        Assert.Throws<ArgumentException>(() => ComicGenre.Create(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => ComicAuthor.Create(Guid.NewGuid(), Guid.Empty));
        Assert.Throws<ArgumentException>(() => ComicTranslationGroup.Create(Guid.NewGuid(), Guid.Empty));
    }

    private static Comic CreateComic(
        Guid? id = null,
        string slug = "the-final-hero",
        string seriesStatus = ComicSeriesStatuses.Ongoing,
        long defaultPriceCoins = 25,
        DateTimeOffset? createdAt = null) => Comic.Create(
            id ?? Guid.NewGuid(),
            slug,
            "The Final Hero",
            "Description",
            seriesStatus,
            "covers/the-final-hero",
            defaultPriceCoins,
            true,
            createdAt ?? Now);
}
