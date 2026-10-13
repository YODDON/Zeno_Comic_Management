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

    public void Publish(
        bool hasValidPage,
        long expectedVersion,
        DateTimeOffset publishedAt)
    {
        EnsureExpectedVersion(expectedVersion);
        EnsureUtc(publishedAt, nameof(publishedAt));

        if (!hasValidPage)
        {
            throw new CatalogRuleViolationException(
                CatalogRuleViolation.PublicationPreconditionFailed,
                "A chapter requires at least one valid page.");
        }

        if (PublicationStatus == ChapterPublicationStatuses.Published)
        {
            return;
        }

        PublicationStatus = ChapterPublicationStatuses.Published;
        Touch(publishedAt);
    }

    public void Hide(
        bool canHide,
        long expectedVersion,
        DateTimeOffset hiddenAt)
    {
        EnsureExpectedVersion(expectedVersion);
        EnsureUtc(hiddenAt, nameof(hiddenAt));

        if (PublicationStatus == ChapterPublicationStatuses.Hidden)
        {
            return;
        }

        if (!canHide)
        {
            throw new CatalogRuleViolationException(
                CatalogRuleViolation.PublicationPreconditionFailed,
                "The last valid published chapter cannot be hidden while its comic is published.");
        }

        PublicationStatus = ChapterPublicationStatuses.Hidden;
        Touch(hiddenAt);
    }

    public void EnsurePageMutationPreservesPublication(bool hasValidPageAfterMutation)
    {
        if (PublicationStatus == ChapterPublicationStatuses.Published &&
            !hasValidPageAfterMutation)
        {
            throw new CatalogRuleViolationException(
                CatalogRuleViolation.PublicationPreconditionFailed,
                "A page mutation cannot leave a published chapter without a valid page.");
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
                "The chapter changed concurrently.");
        }
    }

    private void Touch(DateTimeOffset changedAt)
    {
        Version = checked(Version + 1);
        UpdatedAt = changedAt;
    }
}
