namespace ComicManagement.Catalog.Comics;

public sealed class Comic
{
    public const int MaximumSlugLength = 200;

    private Comic()
    {
    }

    public Guid Id { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public string SeriesStatus { get; private set; } = ComicSeriesStatuses.Ongoing;

    public string PublicationStatus { get; private set; } = ComicPublicationStatuses.Draft;

    public DateTimeOffset? FirstPublishedAt { get; private set; }

    public string? CoverAssetId { get; private set; }

    public long DefaultPriceCoins { get; private set; }

    public bool DefaultVipEligible { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Comic Create(
        Guid id,
        string slug,
        string title,
        string description,
        string seriesStatus,
        string? coverAssetId,
        long defaultPriceCoins,
        bool defaultVipEligible,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Comic ID is required.", nameof(id));
        }

        ValidateSlug(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(description);

        if (!ComicSeriesStatuses.IsDefined(seriesStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(seriesStatus));
        }

        if (coverAssetId is not null && string.IsNullOrWhiteSpace(coverAssetId))
        {
            throw new ArgumentException("Cover asset ID cannot be blank.", nameof(coverAssetId));
        }

        if (defaultPriceCoins < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultPriceCoins));
        }

        EnsureUtc(createdAt, nameof(createdAt));

        return new Comic
        {
            Id = id,
            Slug = slug,
            Title = title,
            Description = description,
            SeriesStatus = seriesStatus,
            PublicationStatus = ComicPublicationStatuses.Draft,
            CoverAssetId = coverAssetId,
            DefaultPriceCoins = defaultPriceCoins,
            DefaultVipEligible = defaultVipEligible,
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    private static void ValidateSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (slug.Length > MaximumSlugLength)
        {
            throw new ArgumentOutOfRangeException(nameof(slug));
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }
}
