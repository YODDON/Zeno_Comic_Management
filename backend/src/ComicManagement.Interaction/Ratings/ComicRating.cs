namespace ComicManagement.Interaction.Ratings;

public sealed class ComicRating
{
    private ComicRating()
    {
    }

    public Guid UserId { get; private set; }

    public Guid ComicId { get; private set; }

    public short Stars { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ComicRating Create(
        Guid userId,
        Guid comicId,
        int stars,
        DateTimeOffset createdAt)
    {
        Validate(userId, comicId, stars, createdAt);
        return new ComicRating
        {
            UserId = userId,
            ComicId = comicId,
            Stars = checked((short)stars),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    public void Update(int stars, DateTimeOffset updatedAt)
    {
        Validate(UserId, ComicId, stars, updatedAt);
        if (updatedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                "Rating update time cannot precede creation time.");
        }

        Stars = checked((short)stars);
        UpdatedAt = updatedAt;
    }

    private static void Validate(
        Guid userId,
        Guid comicId,
        int stars,
        DateTimeOffset timestamp)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (comicId == Guid.Empty)
        {
            throw new ArgumentException("Comic ID is required.", nameof(comicId));
        }

        if (stars is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(stars), "Stars must be between 1 and 5.");
        }

        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Rating timestamp must use UTC.", nameof(timestamp));
        }
    }
}
