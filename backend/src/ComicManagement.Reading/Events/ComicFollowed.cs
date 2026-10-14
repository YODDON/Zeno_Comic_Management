namespace ComicManagement.Reading.Events;

public static partial class ReadingEventTypes
{
    public const string ComicFollowed = "Reading.ComicFollowed.v1";
}

public sealed record ComicFollowed(
    Guid UserId,
    Guid ComicId,
    DateTimeOffset FollowedAt);
