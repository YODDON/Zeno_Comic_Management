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

        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Upload content must be readable.", nameof(content));
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
}
