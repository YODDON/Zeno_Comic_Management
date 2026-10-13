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

    public void Publish(
        bool hasValidPublishedChapter,
        long expectedVersion,
        DateTimeOffset publishedAt)
    {
        EnsureExpectedVersion(expectedVersion);
        EnsureUtc(publishedAt, nameof(publishedAt));

        if (!hasValidPublishedChapter)
        {
            throw new CatalogRuleViolationException(
                CatalogRuleViolation.PublicationPreconditionFailed,
                "A comic requires at least one valid published chapter.");
        }

        if (PublicationStatus == ComicPublicationStatuses.Published)
        {
            return;
        }

        PublicationStatus = ComicPublicationStatuses.Published;
        FirstPublishedAt ??= publishedAt;
        Touch(publishedAt);
    }

    public void Hide(long expectedVersion, DateTimeOffset hiddenAt)
    {
        EnsureExpectedVersion(expectedVersion);
        EnsureUtc(hiddenAt, nameof(hiddenAt));

        if (PublicationStatus == ComicPublicationStatuses.Hidden)
        {
            return;
        }

        PublicationStatus = ComicPublicationStatuses.Hidden;
        Touch(hiddenAt);
    }

    public void ChangeSlug(
        string slug,
        long expectedVersion,
        DateTimeOffset changedAt)
    {
        EnsureExpectedVersion(expectedVersion);
        EnsureUtc(changedAt, nameof(changedAt));
        ValidateSlug(slug);

        if (string.Equals(Slug, slug, StringComparison.Ordinal))
        {
            return;
        }

        if (FirstPublishedAt is not null)
        {
            throw new CatalogRuleViolationException(
                CatalogRuleViolation.SlugLocked,
                "A comic slug cannot change after its first publication.");
        }

        Slug = slug;
        Touch(changedAt);
    }

    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug.Length <= MaximumSlugLength;

    private static void ValidateSlug(string slug)
    {
        if (!IsValidSlug(slug))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(slug);
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

    private void EnsureExpectedVersion(long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            throw new CatalogRuleViolationException(
                CatalogRuleViolation.ConcurrencyConflict,
                "The comic changed concurrently.");
        }
    }

    private void Touch(DateTimeOffset changedAt)
    {
        Version = checked(Version + 1);
        UpdatedAt = changedAt;
    }
}
