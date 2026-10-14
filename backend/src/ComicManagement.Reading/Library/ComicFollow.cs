namespace ComicManagement.Reading.Library;

public sealed class ComicFollow
{
    private ComicFollow()
    {
    }

    public Guid UserId { get; private set; }

    public Guid ComicId { get; private set; }

    public DateTimeOffset FollowedAt { get; private set; }

    public static ComicFollow Create(
        Guid userId,
        Guid comicId,
        DateTimeOffset followedAt)
    {
        if (userId == Guid.Empty || comicId == Guid.Empty)
        {
            throw new ArgumentException("Follow identifiers are required.");
        }

        if (followedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Follow timestamp must use UTC.", nameof(followedAt));
        }

        return new ComicFollow
        {
            UserId = userId,
            ComicId = comicId,
            FollowedAt = followedAt,
        };
    }
}
