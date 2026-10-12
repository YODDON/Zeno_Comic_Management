using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Media;
using Xunit;

namespace ComicManagement.Catalog.UnitTests;

public sealed class UploadChapterPageHandlerTests
{
    [Fact]
    public async Task Handler_persists_pending_intent_before_external_upload_and_marks_it_ready()
    {
        var calls = new List<string>();
        var assetStore = new FakeAssetStore(calls);
        var pageStore = new FakePageStore(calls);
        var handler = new UploadChapterPageHandler(assetStore, pageStore);

        var page = await handler.HandleAsync(CreateCommand());

        Assert.Equal(["persist-pending", "upload", "mark-ready"], calls);
        Assert.True(page.IsValid);
        Assert.Equal(1, assetStore.UploadCount);
    }

    [Fact]
    public async Task Handler_returns_an_existing_ready_page_without_uploading_again()
    {
        var calls = new List<string>();
        var assetStore = new FakeAssetStore(calls);
        var pageStore = new FakePageStore(calls) { ReturnReadyPage = true };
        var handler = new UploadChapterPageHandler(assetStore, pageStore);

        var page = await handler.HandleAsync(CreateCommand());

        Assert.True(page.IsValid);
        Assert.Equal(["persist-pending"], calls);
        Assert.Equal(0, assetStore.UploadCount);
    }

    [Fact]
    public async Task Failed_upload_leaves_the_persisted_pending_intent_for_a_safe_retry()
    {
        var calls = new List<string>();
        var assetStore = new FakeAssetStore(calls) { FailUpload = true };
        var pageStore = new FakePageStore(calls);
        var handler = new UploadChapterPageHandler(assetStore, pageStore);

        await Assert.ThrowsAsync<ChapterPageAssetException>(() => handler.HandleAsync(CreateCommand()));

        Assert.Equal(["persist-pending", "upload"], calls);
        Assert.NotNull(pageStore.Page);
        Assert.Equal(ChapterPageAssetStatuses.Pending, pageStore.Page.AssetStatus);
    }

    [Fact]
    public async Task Invalid_upload_is_rejected_before_a_pending_intent_is_persisted()
    {
        var calls = new List<string>();
        var handler = new UploadChapterPageHandler(
            new FakeAssetStore(calls),
            new FakePageStore(calls));
        var command = CreateCommand() with { ContentType = "application/pdf" };

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));

        Assert.Empty(calls);
    }

    private static UploadChapterPageCommand CreateCommand() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        1,
        "page.webp",
        "image/webp",
        new MemoryStream([1, 2, 3]));

    private sealed class FakeAssetStore(List<string> calls) : IChapterPageAssetStore
    {
        public int UploadCount { get; private set; }

        public bool FailUpload { get; init; }

        public string GetPublicId(Guid chapterId, Guid pageId) =>
            $"comic-management/chapter-pages/{chapterId:N}/{pageId:N}";

        public Task<ChapterPageAsset> UploadAsync(
            ChapterPageAssetUpload upload,
            CancellationToken cancellationToken = default)
        {
            calls.Add("upload");
            UploadCount++;
            if (FailUpload)
            {
                throw new ChapterPageAssetException(
                    ChapterPageAssetErrorCodes.ProviderUnavailable,
                    "Provider unavailable.");
            }

            return Task.FromResult(new ChapterPageAsset(
                GetPublicId(upload.ChapterId, upload.PageId),
                1200,
                1800));
        }

        public Task DeleteAsync(string publicId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakePageStore(List<string> calls) : IChapterPageUploadStore
    {
        public bool ReturnReadyPage { get; init; }

        public ChapterPage? Page { get; private set; }

        public Task<ChapterPage> GetOrCreatePendingAsync(
            ChapterPage pendingPage,
            CancellationToken cancellationToken = default)
        {
            calls.Add("persist-pending");
            Page ??= pendingPage;
            if (ReturnReadyPage && !Page.IsValid)
            {
                Page.MarkReady(1200, 1800);
            }

            return Task.FromResult(Page);
        }

        public Task<ChapterPage> MarkReadyAsync(
            Guid pageId,
            ChapterPageAsset asset,
            CancellationToken cancellationToken = default)
        {
            calls.Add("mark-ready");
            Assert.NotNull(Page);
            Assert.Equal(pageId, Page.Id);
            Assert.Equal(asset.PublicId, Page.CloudinaryPublicId);
            Page.MarkReady(asset.Width, asset.Height);
            return Task.FromResult(Page);
        }
    }
}
