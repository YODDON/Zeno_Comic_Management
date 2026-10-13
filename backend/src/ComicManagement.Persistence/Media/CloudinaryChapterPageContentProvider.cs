using CloudinaryDotNet;
using ComicManagement.Reading.Access;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicManagement.Persistence;

internal sealed class CloudinaryChapterPageContentProvider(
    Cloudinary cloudinary,
    HttpClient httpClient,
    IOptions<CloudinaryOptions> options,
    ILogger<CloudinaryChapterPageContentProvider> logger)
    : IChapterPageContentProvider
{
    private const string AuthenticatedAssetType = "authenticated";
    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "image/avif",
            "image/gif",
            "image/jpeg",
            "image/png",
            "image/webp",
        };

    private readonly CloudinaryOptions _options = options.Value;

    public async Task<ChapterPageContent?> GetAsync(
        string assetKey,
        CancellationToken cancellationToken = default)
    {
        if (!IsOwnedAssetKey(assetKey))
        {
            logger.LogWarning("Rejected a chapter page outside the configured asset namespace.");
            return null;
        }

        Uri providerUri;
        try
        {
            var candidate = cloudinary.Api.UrlImgUp
                .Secure(true)
                .Signed(true)
                .Type(AuthenticatedAssetType)
                .BuildUrl(assetKey);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out providerUri!) ||
                providerUri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogWarning("The media provider generated an invalid chapter page URL.");
                return null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "The media provider could not create a chapter page request ({ExceptionType}).",
                exception.GetType().Name);
            return null;
        }

        try
        {
            using var response = await httpClient.GetAsync(
                providerUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "The media provider rejected a chapter page request with status {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !AllowedContentTypes.Contains(contentType))
            {
                logger.LogWarning("The media provider returned an unsupported chapter page type.");
                return null;
            }

            if (response.Content.Headers.ContentLength is > 0 and var contentLength &&
                contentLength > _options.MaximumChapterPageBytes)
            {
                logger.LogWarning("The media provider returned an oversized chapter page.");
                return null;
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new MemoryStream();
            var buffer = new byte[81920];
            long totalBytes = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytes += bytesRead;
                if (totalBytes > _options.MaximumChapterPageBytes)
                {
                    logger.LogWarning("The media provider streamed an oversized chapter page.");
                    return null;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            return totalBytes == 0
                ? null
                : new ChapterPageContent(destination.ToArray(), contentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogWarning(
                "The media provider could not return a chapter page ({ExceptionType}).",
                exception.GetType().Name);
            return null;
        }
    }

    private bool IsOwnedAssetKey(string assetKey)
    {
        if (string.IsNullOrWhiteSpace(assetKey))
        {
            return false;
        }

        var prefix = _options.ChapterPagePublicIdPrefix.Trim('/');
        return prefix.Length > 0 &&
               assetKey.StartsWith($"{prefix}/", StringComparison.Ordinal) &&
               !assetKey.Contains("..", StringComparison.Ordinal) &&
               !assetKey.Contains('\\');
    }
}
