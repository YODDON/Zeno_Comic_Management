using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog;
using Xunit;

namespace ComicManagement.Catalog.UnitTests;

public sealed class AdminEditorModelTests
{
    [Fact]
    public void Comic_metadata_update_keeps_publication_state_and_advances_version()
    {
        var now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        var comic = Comic.Create(Guid.NewGuid(), "slug", "Old", "Old description",
            ComicSeriesStatuses.Ongoing, null, 0, false, now);

        comic.UpdateMetadata("New", "New description", ComicSeriesStatuses.Completed,
            "cover", 50, true, 1, now.AddMinutes(1));

        Assert.Equal("New", comic.Title);
        Assert.Equal(ComicPublicationStatuses.Draft, comic.PublicationStatus);
        Assert.Equal(2, comic.Version);
        Assert.Equal(50, comic.DefaultPriceCoins);
    }

    [Fact]
    public void Chapter_metadata_update_rejects_stale_version()
    {
        var now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        var chapter = Chapter.Create(Guid.NewGuid(), Guid.NewGuid(), "1", 1, null, null, now);

        chapter.UpdateMetadata("2", 2, 0, true, 1, now.AddMinutes(1));

        Assert.Throws<CatalogRuleViolationException>(() => chapter.UpdateMetadata(
            "3", 3, null, null, 1, now.AddMinutes(2)));
    }

    [Fact]
    public void Full_editor_update_changes_slug_and_metadata_with_one_version_increment()
    {
        var now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        var comic = Comic.Create(Guid.NewGuid(), "old", "Old", "", ComicSeriesStatuses.Ongoing,
            null, 0, false, now);

        comic.UpdateEditorState("new", "New", "Description", ComicSeriesStatuses.Completed,
            "cover", 10, true, 1, now.AddMinutes(1));

        Assert.Equal("new", comic.Slug);
        Assert.Equal("New", comic.Title);
        Assert.Equal(2, comic.Version);
    }

    [Fact]
    public void Full_editor_cannot_bypass_permanent_slug_lock()
    {
        var now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        var comic = Comic.Create(Guid.NewGuid(), "locked", "Original", "", ComicSeriesStatuses.Ongoing,
            null, 0, false, now);
        comic.Publish(true, 1, now.AddMinutes(1));

        var exception = Assert.Throws<CatalogRuleViolationException>(() => comic.UpdateEditorState(
            "changed", "Changed", "", ComicSeriesStatuses.Completed, null, 0, false,
            2, now.AddMinutes(2)));

        Assert.Equal(CatalogRuleViolation.SlugLocked, exception.Violation);
        Assert.Equal("locked", comic.Slug);
        Assert.Equal("Original", comic.Title);
        Assert.Equal(2, comic.Version);
    }
}
