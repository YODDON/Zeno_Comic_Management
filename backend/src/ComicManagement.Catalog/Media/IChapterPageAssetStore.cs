namespace ComicManagement.Catalog.Media;

public interface IChapterPageAssetStore
{
    Task<ChapterPageAsset> UploadAsync(
        ChapterPageAssetUpload upload,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string publicId, CancellationToken cancellationToken = default);
}
