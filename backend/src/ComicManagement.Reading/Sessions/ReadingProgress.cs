namespace ComicManagement.Reading.Sessions;

public sealed class ReadingProgress
{
    private ReadingProgress()
    {
    }

    public Guid UserId { get; private set; }

    public Guid ComicId { get; private set; }

    public Guid LastChapterId { get; private set; }

    public int LastPageOrder { get; private set; }

    public decimal LastPageOffsetRatio { get; private set; }

    public int AcceptedMaxProgressBasisPoints { get; private set; }

    public DateTimeOffset LastReadAt { get; private set; }

    public Guid SourceSessionId { get; private set; }

    public DateTimeOffset SourceSessionStartedAt { get; private set; }

    public long Version { get; private set; }

    public static ReadingProgress Create(
        Guid userId,
        Guid comicId,
        Guid chapterId,
        int pageOrder,
        decimal pageOffsetRatio,
        int acceptedMaxProgressBasisPoints,
        DateTimeOffset readAt,
        Guid sourceSessionId,
        DateTimeOffset sourceSessionStartedAt)
    {
        Validate(
            userId,
            comicId,
            chapterId,
            pageOrder,
            pageOffsetRatio,
            acceptedMaxProgressBasisPoints,
            readAt,
            sourceSessionId,
            sourceSessionStartedAt);

        return new ReadingProgress
        {
            UserId = userId,
            ComicId = comicId,
            LastChapterId = chapterId,
            LastPageOrder = pageOrder,
            LastPageOffsetRatio = pageOffsetRatio,
            AcceptedMaxProgressBasisPoints = acceptedMaxProgressBasisPoints,
            LastReadAt = readAt,
            SourceSessionId = sourceSessionId,
            SourceSessionStartedAt = sourceSessionStartedAt,
            Version = 1,
        };
    }

    public bool Apply(
        Guid chapterId,
        int pageOrder,
        decimal pageOffsetRatio,
        int acceptedMaxProgressBasisPoints,
        DateTimeOffset readAt,
        Guid sourceSessionId,
        DateTimeOffset sourceSessionStartedAt)
    {
        Validate(
            UserId,
            ComicId,
            chapterId,
            pageOrder,
            pageOffsetRatio,
            acceptedMaxProgressBasisPoints,
            readAt,
            sourceSessionId,
            sourceSessionStartedAt);

        if (IsOlderSession(sourceSessionStartedAt, sourceSessionId))
        {
            return false;
        }

        LastChapterId = chapterId;
        LastPageOrder = pageOrder;
        LastPageOffsetRatio = pageOffsetRatio;
        AcceptedMaxProgressBasisPoints = acceptedMaxProgressBasisPoints;
        LastReadAt = readAt;
        SourceSessionId = sourceSessionId;
        SourceSessionStartedAt = sourceSessionStartedAt;
        Version = checked(Version + 1);
        return true;
    }

    private bool IsOlderSession(DateTimeOffset startedAt, Guid sessionId) =>
        startedAt < SourceSessionStartedAt ||
        (startedAt == SourceSessionStartedAt &&
         sessionId.CompareTo(SourceSessionId) < 0);

    private static void Validate(
        Guid userId,
        Guid comicId,
        Guid chapterId,
        int pageOrder,
        decimal pageOffsetRatio,
        int acceptedMaxProgressBasisPoints,
        DateTimeOffset readAt,
        Guid sourceSessionId,
        DateTimeOffset sourceSessionStartedAt)
    {
        if (userId == Guid.Empty ||
            comicId == Guid.Empty ||
            chapterId == Guid.Empty ||
            sourceSessionId == Guid.Empty ||
            pageOrder < 1 ||
            pageOffsetRatio is < 0 or > 1 ||
            acceptedMaxProgressBasisPoints is < 0 or > 10_000)
        {
            throw new ArgumentException("The reading progress state is invalid.");
        }

        if (readAt.Offset != TimeSpan.Zero || sourceSessionStartedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Reading progress timestamps must use UTC.");
        }
    }
}
