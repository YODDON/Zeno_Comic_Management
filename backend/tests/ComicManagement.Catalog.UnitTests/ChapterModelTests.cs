using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Media;
using Xunit;

namespace ComicManagement.Catalog.UnitTests;

public sealed class ChapterModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(25L)]
    public void Chapter_supports_inherited_free_and_paid_price_overrides(long? priceOverrideCoins)
    {
        var chapter = Chapter.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Chapter 1",
            1,
            priceOverrideCoins,
            null,
            Now);

        Assert.Equal(priceOverrideCoins, chapter.PriceOverrideCoins);
        Assert.Null(chapter.VipOverride);
        Assert.Equal(ChapterPublicationStatuses.Draft, chapter.PublicationStatus);
        Assert.Equal(1, chapter.Version);
        Assert.Equal(Now, chapter.CreatedAt);
        Assert.Equal(Now, chapter.UpdatedAt);
    }

    [Fact]
    public void Chapter_rejects_invalid_required_values_price_and_timestamp()
    {
        Assert.Throws<ArgumentException>(() => CreateChapter(id: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateChapter(comicId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateChapter(chapterLabel: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateChapter(chapterLabel: new string('a', Chapter.MaximumLabelLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateChapter(priceOverrideCoins: -1));
        Assert.Throws<ArgumentException>(() =>
            CreateChapter(createdAt: Now.ToOffset(TimeSpan.FromHours(7))));
    }

    [Fact]
    public void Chapter_page_is_valid_only_after_a_pending_asset_is_marked_ready()
    {
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "comic-management/chapter/page");

        Assert.Equal(ChapterPageAssetStatuses.Pending, page.AssetStatus);
        Assert.False(page.IsValid);
        Assert.Equal(0, page.Width);
        Assert.Equal(0, page.Height);

        page.MarkReady(1200, 1800);

        Assert.Equal(ChapterPageAssetStatuses.Ready, page.AssetStatus);
        Assert.True(page.IsValid);
        Assert.Equal(1200, page.Width);
        Assert.Equal(1800, page.Height);

        Assert.Throws<ArgumentException>(() => CreatePage(id: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreatePage(chapterId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreatePage(publicId: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePage().MarkReady(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePage().MarkReady(100, 0));
    }

    [Fact]
    public void Upload_request_rejects_non_image_or_unreadable_content()
    {
        Assert.Throws<ArgumentException>(() => CreateUpload(contentType: "application/pdf"));
        Assert.Throws<ArgumentException>(() => CreateUpload(content: new UnreadableStream()));
    }

    private static Chapter CreateChapter(
        Guid? id = null,
        Guid? comicId = null,
        string chapterLabel = "Chapter 1",
        long? priceOverrideCoins = null,
        DateTimeOffset? createdAt = null) => Chapter.Create(
            id ?? Guid.NewGuid(),
            comicId ?? Guid.NewGuid(),
            chapterLabel,
            1,
            priceOverrideCoins,
            null,
            createdAt ?? Now);

    private static ChapterPage CreatePage(
        Guid? id = null,
        Guid? chapterId = null,
        string publicId = "comic-management/chapter/page") => ChapterPage.CreatePending(
            id ?? Guid.NewGuid(),
            chapterId ?? Guid.NewGuid(),
            1,
            publicId);

    private static ChapterPageAssetUpload CreateUpload(
        string contentType = "image/webp",
        Stream? content = null) => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "page.webp",
            contentType,
            content ?? new MemoryStream([1, 2, 3]));

    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
