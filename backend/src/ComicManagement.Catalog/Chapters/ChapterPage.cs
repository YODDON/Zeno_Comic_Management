namespace ComicManagement.Catalog.Chapters;

public sealed class ChapterPage
{
    public const int MaximumAssetStatusLength = 24;
    public const int MaximumUploadFingerprintLength = 80;

    private ChapterPage()
    {
    }

    public Guid Id { get; private set; }

    public Guid ChapterId { get; private set; }

    public int PageOrder { get; private set; }

    public string CloudinaryPublicId { get; private set; } = string.Empty;

    public string AssetStatus { get; private set; } = string.Empty;

    public string UploadFingerprint { get; private set; } = string.Empty;

    public DateTimeOffset UploadCreatedAt { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsValid =>
        AssetStatus == ChapterPageAssetStatuses.Ready && Width > 0 && Height > 0;

    public static ChapterPage CreatePending(
        Guid id,
        Guid chapterId,
        int pageOrder,
        string cloudinaryPublicId,
        string uploadFingerprint,
        DateTimeOffset uploadCreatedAt)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadFingerprint);
        if (uploadFingerprint.Length > MaximumUploadFingerprintLength ||
            !uploadFingerprint.StartsWith("sha256:", StringComparison.Ordinal) ||
            uploadFingerprint.Length != "sha256:".Length + 64 ||
            uploadFingerprint["sha256:".Length..].Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Upload fingerprint must be a lowercase SHA-256 value.", nameof(uploadFingerprint));
        }

        if (uploadCreatedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Upload creation time must be UTC.", nameof(uploadCreatedAt));
        }

        return new ChapterPage
        {
            Id = id,
            ChapterId = chapterId,
            PageOrder = pageOrder,
            CloudinaryPublicId = cloudinaryPublicId,
            AssetStatus = ChapterPageAssetStatuses.Pending,
            UploadFingerprint = uploadFingerprint,
            UploadCreatedAt = uploadCreatedAt,
            Width = 0,
            Height = 0,
        };
    }

    public void MarkReady(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        AssetStatus = ChapterPageAssetStatuses.Ready;
        Width = width;
        Height = height;
    }
}
