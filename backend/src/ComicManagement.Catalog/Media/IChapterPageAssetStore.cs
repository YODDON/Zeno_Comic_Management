namespace ComicManagement.Catalog.Media;

public interface IChapterPageAssetStore
{
    void ValidateUpload(ChapterPageAssetUpload upload);

    string GetPublicId(Guid chapterId, Guid pageId);

    Task<ChapterPageAsset> UploadAsync(
        ChapterPageAssetUpload upload,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string publicId, CancellationToken cancellationToken = default);
}
