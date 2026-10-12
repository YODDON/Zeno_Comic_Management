using ComicManagement.Catalog.Chapters;
using ComicManagement.Shared.Time;

namespace ComicManagement.Catalog.Media;

public sealed record UploadChapterPageCommand(
    Guid ChapterId,
    Guid PageId,
    int PageOrder,
    string FileName,
    string ContentType,
    Stream Content);

public interface IChapterPageUploadStore
{
    Task<ChapterPage> GetOrCreatePendingAsync(
        ChapterPage pendingPage,
        CancellationToken cancellationToken = default);

    Task<ChapterPage> MarkReadyAsync(
        Guid pageId,
        ChapterPageAsset asset,
        CancellationToken cancellationToken = default);
}

public sealed class ChapterPageUploadConflictException(string message) : Exception(message);

public sealed class UploadChapterPageHandler(
    IChapterPageAssetStore assetStore,
    IChapterPageUploadStore pageStore,
    IClock clock)
{
    public async Task<ChapterPage> HandleAsync(
        UploadChapterPageCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var upload = new ChapterPageAssetUpload(
            command.ChapterId,
            command.PageId,
            command.FileName,
            command.ContentType,
            command.Content);
        assetStore.ValidateUpload(upload);
        var uploadFingerprint = await upload.ComputeFingerprintAsync(cancellationToken);
        var publicId = assetStore.GetPublicId(command.ChapterId, command.PageId);
        var pendingPage = ChapterPage.CreatePending(
            command.PageId,
            command.ChapterId,
            command.PageOrder,
            publicId,
            uploadFingerprint,
            clock.UtcNow);
        var persistedPage = await pageStore.GetOrCreatePendingAsync(pendingPage, cancellationToken);

        if (persistedPage.IsValid)
        {
            return persistedPage;
        }

        var asset = await assetStore.UploadAsync(upload, cancellationToken);

        return await pageStore.MarkReadyAsync(command.PageId, asset, cancellationToken);
    }
}
