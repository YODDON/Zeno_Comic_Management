namespace ComicManagement.Reading.Events;

public static class ReadingEventTypes
{
    public const string ChapterReadQualified = "Reading.ChapterReadQualified.v1";
}

public sealed record ChapterReadQualified(
    Guid SessionId,
    Guid UserId,
    Guid ComicId,
    Guid ChapterId,
    DateTimeOffset QualifiedAt,
    string PolicyRevision,
    int MinimumProgressBasisPoints,
    long MinimumActiveDurationMs,
    string ManifestRevision);
