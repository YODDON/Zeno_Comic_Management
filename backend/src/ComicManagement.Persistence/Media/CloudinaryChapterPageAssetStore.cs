using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using ComicManagement.Catalog.Media;
using Microsoft.Extensions.Options;

namespace ComicManagement.Persistence;

internal sealed class CloudinaryChapterPageAssetStore(
    ICloudinaryUploadApi cloudinary,
    ICloudinaryAdminApi cloudinaryAdmin,
    IOptions<CloudinaryOptions> options) : IChapterPageAssetStore
{
    private const string AuthenticatedAssetType = "authenticated";
    private const string SafeProviderMessage = "The media provider could not complete the request.";

    private readonly CloudinaryOptions _options = options.Value;

    public string GetPublicId(Guid chapterId, Guid pageId)
    {
        if (chapterId == Guid.Empty)
        {
            throw new ArgumentException("Chapter ID is required.", nameof(chapterId));
        }

        if (pageId == Guid.Empty)
        {
            throw new ArgumentException("Page ID is required.", nameof(pageId));
        }

        return string.Join(
            '/',
            NormalizePrefix(_options.ChapterPagePublicIdPrefix),
            chapterId.ToString("N"),
            pageId.ToString("N"));
    }

    public async Task<ChapterPageAsset> UploadAsync(
        ChapterPageAssetUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var expectedPublicId = GetPublicId(upload.ChapterId, upload.PageId);
        var parameters = new ImageUploadParams
        {
            File = new FileDescription(upload.FileName, upload.Content),
            PublicId = expectedPublicId,
            Type = AuthenticatedAssetType,
            Overwrite = false,
            UseFilename = false,
            DiscardOriginalFilename = true,
        };

        ImageUploadResult? result;
        try
        {
            result = await cloudinary.UploadAsync(parameters, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var recoveredAsset = await TryRecoverAsync(expectedPublicId, cancellationToken);
            if (recoveredAsset is not null)
            {
                return recoveredAsset;
            }

            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.ProviderUnavailable,
                SafeProviderMessage);
        }

        if (result is null || result.Error is not null)
        {
            var recoveredAsset = await TryRecoverAsync(expectedPublicId, cancellationToken);
            if (recoveredAsset is not null)
            {
                return recoveredAsset;
            }

            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.ProviderRejected,
                SafeProviderMessage);
        }

        return ValidateAsset(
            result.PublicId,
            result.Type,
            result.ResourceType,
            result.Width,
            result.Height,
            expectedPublicId);
    }

    public async Task DeleteAsync(string publicId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        EnsureOwnedPublicId(publicId);

        var parameters = new DeletionParams(publicId)
        {
            Type = AuthenticatedAssetType,
            ResourceType = ResourceType.Image,
            Invalidate = true,
        };

        DeletionResult? result;
        try
        {
            result = await cloudinary.DestroyAsync(parameters).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.CleanupFailed,
                SafeProviderMessage);
        }

        if (result is null || result.Error is not null ||
            result.Result is not ("ok" or "not found"))
        {
            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.CleanupFailed,
                SafeProviderMessage);
        }
    }

    private async Task<ChapterPageAsset?> TryRecoverAsync(
        string expectedPublicId,
        CancellationToken cancellationToken)
    {
        GetResourceResult? result;
        try
        {
            result = await cloudinaryAdmin.GetResourceAsync(
                new GetResourceParams(expectedPublicId)
                {
                    ResourceType = ResourceType.Image,
                    Type = AuthenticatedAssetType,
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (result is null || result.Error is not null)
        {
            return null;
        }

        return ValidateAsset(
            result.PublicId,
            result.Type,
            result.ResourceType.ToString().ToLowerInvariant(),
            result.Width,
            result.Height,
            expectedPublicId);
    }

    private static ChapterPageAsset ValidateAsset(
        string? publicId,
        string? assetType,
        string? resourceType,
        int width,
        int height,
        string expectedPublicId)
    {
        if (!string.Equals(publicId, expectedPublicId, StringComparison.Ordinal) ||
            !string.Equals(assetType, AuthenticatedAssetType, StringComparison.Ordinal) ||
            !string.Equals(resourceType, "image", StringComparison.OrdinalIgnoreCase) ||
            width <= 0 ||
            height <= 0)
        {
            throw new ChapterPageAssetException(
                ChapterPageAssetErrorCodes.InvalidProviderResponse,
                "The media provider returned inconsistent asset metadata.");
        }

        return new ChapterPageAsset(expectedPublicId, width, height);
    }

    private void EnsureOwnedPublicId(string publicId)
    {
        var ownedPrefix = $"{NormalizePrefix(_options.ChapterPagePublicIdPrefix)}/";
        if (!publicId.StartsWith(ownedPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The asset does not belong to the chapter-page namespace.",
                nameof(publicId));
        }
    }

    private static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var normalized = prefix.Trim('/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The asset prefix must contain a path segment.", nameof(prefix));
        }

        return normalized;
    }
}
