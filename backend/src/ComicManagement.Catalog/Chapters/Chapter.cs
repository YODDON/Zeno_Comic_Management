namespace ComicManagement.Catalog.Chapters;

public sealed class Chapter
{
    public const int MaximumLabelLength = 120;

    private Chapter()
    {
    }

    public Guid Id { get; private set; }

    public Guid ComicId { get; private set; }

    public string ChapterLabel { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    public long? PriceOverrideCoins { get; private set; }

    public bool? VipOverride { get; private set; }

    public string PublicationStatus { get; private set; } = ChapterPublicationStatuses.Draft;

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Chapter Create(
        Guid id,
        Guid comicId,
        string chapterLabel,
        int sortOrder,
        long? priceOverrideCoins,
        bool? vipOverride,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Chapter ID is required.", nameof(id));
        }

        if (comicId == Guid.Empty)
        {
            throw new ArgumentException("Comic ID is required.", nameof(comicId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(chapterLabel);
        if (chapterLabel.Length > MaximumLabelLength)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterLabel));
        }

        if (priceOverrideCoins < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priceOverrideCoins));
        }

        EnsureUtc(createdAt, nameof(createdAt));

        return new Chapter
        {
            Id = id,
            ComicId = comicId,
            ChapterLabel = chapterLabel,
            SortOrder = sortOrder,
            PriceOverrideCoins = priceOverrideCoins,
            VipOverride = vipOverride,
            PublicationStatus = ChapterPublicationStatuses.Draft,
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }
}
