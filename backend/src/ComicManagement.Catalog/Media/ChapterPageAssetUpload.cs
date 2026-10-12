using System.Security.Cryptography;

namespace ComicManagement.Catalog.Media;

public sealed class ChapterPageAssetUpload
{
    public ChapterPageAssetUpload(
        Guid chapterId,
        Guid pageId,
        string fileName,
        string contentType,
        Stream content)
    {
        if (chapterId == Guid.Empty)
        {
            throw new ArgumentException("Chapter ID is required.", nameof(chapterId));
        }

        if (pageId == Guid.Empty)
        {
            throw new ArgumentException("Page ID is required.", nameof(pageId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Chapter pages must use an image content type.", nameof(contentType));
        }

        if (contentType.Length == "image/".Length ||
            contentType["image/".Length..].Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '+' and not '-'))
        {
            throw new ArgumentException("Chapter page content type is invalid.", nameof(contentType));
        }

        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Upload content must be readable.", nameof(content));
        }

        if (!content.CanSeek)
        {
            throw new ArgumentException(
                "Upload content must be seekable so its identity can be verified before provider upload.",
                nameof(content));
        }

        if (content.Length <= content.Position)
        {
            throw new ArgumentException("Upload content must not be empty.", nameof(content));
        }

        ChapterId = chapterId;
        PageId = pageId;
        FileName = fileName;
        ContentType = contentType;
        Content = content;
    }

    public Guid ChapterId { get; }

    public Guid PageId { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public Stream Content { get; }

    public long ContentLength => Content.Length - Content.Position;

    public async Task<string> ComputeFingerprintAsync(CancellationToken cancellationToken = default)
    {
        var originalPosition = Content.Position;
        try
        {
            var hash = await SHA256.HashDataAsync(Content, cancellationToken);
            return $"sha256:{Convert.ToHexStringLower(hash)}";
        }
        finally
        {
            Content.Position = originalPosition;
        }
    }
}
