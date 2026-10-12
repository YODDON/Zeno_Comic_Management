using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using ComicManagement.Catalog.Media;
using Microsoft.Extensions.Options;

namespace ComicManagement.Persistence;

internal sealed class CloudinaryChapterPageAssetStore(
    ICloudinaryUploadApi cloudinary,
    IOptions<CloudinaryOptions> options) : IChapterPageAssetStore
{
    private const string AuthenticatedAssetType = "authenticated";
    private const string SafeProviderMessage = "The media provider could not complete the request.";

    private readonly CloudinaryOptions _options = options.Value;

    public async Task<ChapterPageAsset> UploadAsync(
        ChapterPageAssetUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var parameters = new ImageUploadParams
        {
            File = new FileDescription(upload.FileName, upload.Content),
            PublicId = BuildPublicId(upload.ChapterId, upload.PageId),
            Type = AuthenticatedAssetType,
            Overwrite = false,
            UseFilename = false,
            DiscardOriginalFilename = true,
        };

        ImageUploadResult result;
        try
        {
            result = await cloudinary.UploadAsync(parameters, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not ChapterPageAssetException)
        {
            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.ProviderUnavailable,
                SafeProviderMessage,
                exception);
        }

        if (result.Error is not null)
        {
            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.ProviderRejected,
                SafeProviderMessage);
        }

        if (string.IsNullOrWhiteSpace(result.PublicId) || result.Width <= 0 || result.Height <= 0)
        {
            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.InvalidProviderResponse,
                "The media provider returned incomplete asset metadata.");
        }

        return new ChapterPageAsset(result.PublicId, result.Width, result.Height);
    }

    public async Task DeleteAsync(string publicId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);

        var parameters = new DeletionParams(publicId)
        {
            Type = AuthenticatedAssetType,
            ResourceType = ResourceType.Image,
            Invalidate = true,
        };

        DeletionResult result;
        try
        {
            result = await cloudinary.DestroyAsync(parameters).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not ChapterPageAssetException)
        {
            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.CleanupFailed,
                SafeProviderMessage,
                exception);
        }

        if (result.Error is not null ||
            result.Result is not ("ok" or "not found"))
        {
            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.CleanupFailed,
                SafeProviderMessage);
        }
    }

    private string BuildPublicId(Guid chapterId, Guid pageId) => string.Join(
        '/',
        _options.ChapterPagePublicIdPrefix.Trim('/'),
        chapterId.ToString("N"),
        pageId.ToString("N"));
}
