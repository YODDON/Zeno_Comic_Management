namespace ComicManagement.Catalog.Chapters;

public sealed class ChapterPage
{
    public const int MaximumAssetStatusLength = 24;

    private ChapterPage()
    {
    }

    public Guid Id { get; private set; }

    public Guid ChapterId { get; private set; }

    public int PageOrder { get; private set; }

    public string CloudinaryPublicId { get; private set; } = string.Empty;

    public string AssetStatus { get; private set; } = string.Empty;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public static ChapterPage Create(
        Guid id,
        Guid chapterId,
        int pageOrder,
        string cloudinaryPublicId,
        string assetStatus,
        int width,
        int height)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Chapter page ID is required.", nameof(id));
        }

        if (chapterId == Guid.Empty)
        {
            throw new ArgumentException("Chapter ID is required.", nameof(chapterId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(cloudinaryPublicId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetStatus);
        if (assetStatus.Length > MaximumAssetStatusLength)
        {
            throw new ArgumentOutOfRangeException(nameof(assetStatus));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        return new ChapterPage
        {
            Id = id,
            ChapterId = chapterId,
            PageOrder = pageOrder,
            CloudinaryPublicId = cloudinaryPublicId,
            AssetStatus = assetStatus,
            Width = width,
            Height = height,
        };
    }
}
