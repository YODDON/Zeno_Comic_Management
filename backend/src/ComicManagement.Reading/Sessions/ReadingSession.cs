namespace ComicManagement.Reading.Sessions;

public static class ReadingSessionStatuses
{
    public const string Active = "Active";
    public const string Qualified = "Qualified";
}

public static class ReadingSequenceConflictReasons
{
    public const string PayloadMismatch = "PayloadMismatch";
    public const string Stale = "Stale";
    public const string Gap = "Gap";
}

public sealed record ReadingQualificationPolicy(
    string Revision,
    int MinimumProgressBasisPoints,
    long MinimumActiveDurationMs)
{
    public static ReadingQualificationPolicy R02V1 { get; } = new(
        "r02-v1",
        9_000,
        15_000);
}

public enum ReadingHeartbeatOutcome
{
    Accepted = 0,
    Replayed = 1,
    SequenceConflict = 2,
    Expired = 3,
}

public sealed record ReadingHeartbeatApplication(
    ReadingHeartbeatOutcome Outcome,
    string? ConflictReason = null);

public enum ReadingQualificationOutcome
{
    Qualified = 0,
    AlreadyQualified = 1,
    Expired = 2,
    ProgressThresholdNotMet = 3,
    ActiveDurationThresholdNotMet = 4,
}

public sealed class ReadingSession
{
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromMinutes(30);
    public const long MaximumHeartbeatCreditMs = 15_000;

    private ReadingSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid ComicId { get; private set; }

    public Guid ChapterId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? LastActivityAt { get; private set; }

    public bool LastActivityWasReady { get; private set; }

    public long ActiveDurationMs { get; private set; }

    public int? CurrentPageOrder { get; private set; }

    public decimal? CurrentPageOffsetRatio { get; private set; }

    public int MaxProgressBasisPoints { get; private set; }

    public string ManifestRevision { get; private set; } = string.Empty;

    public int ManifestPageCount { get; private set; }

    public long LastAcceptedSequence { get; private set; }

    public string? LastRequestFingerprint { get; private set; }

    public int MinimumProgressBasisPoints { get; private set; }

    public long MinimumActiveDurationMs { get; private set; }

    public string PolicyRevision { get; private set; } = string.Empty;

    public DateTimeOffset? QualifiedAt { get; private set; }

    public string Status { get; private set; } = ReadingSessionStatuses.Active;

    public long Version { get; private set; }

    public static ReadingSession Create(
        Guid id,
        Guid userId,
        Guid comicId,
        Guid chapterId,
        string manifestRevision,
        int manifestPageCount,
        ReadingQualificationPolicy policy,
        DateTimeOffset startedAt)
    {
        EnsureIdentifier(id, nameof(id));
        EnsureIdentifier(userId, nameof(userId));
        EnsureIdentifier(comicId, nameof(comicId));
        EnsureIdentifier(chapterId, nameof(chapterId));
        EnsureManifest(manifestRevision, manifestPageCount);
        ArgumentNullException.ThrowIfNull(policy);
        EnsurePolicy(policy);
        EnsureUtc(startedAt, nameof(startedAt));

        return new ReadingSession
        {
            Id = id,
            UserId = userId,
            ComicId = comicId,
            ChapterId = chapterId,
            StartedAt = startedAt,
            ExpiresAt = startedAt.Add(AbsoluteLifetime),
            ManifestRevision = manifestRevision,
            ManifestPageCount = manifestPageCount,
            MinimumProgressBasisPoints = policy.MinimumProgressBasisPoints,
            MinimumActiveDurationMs = policy.MinimumActiveDurationMs,
            PolicyRevision = policy.Revision,
            Status = ReadingSessionStatuses.Active,
            Version = 1,
        };
    }

    public ReadingHeartbeatApplication ClassifySequence(
        long sequenceNumber,
        string requestFingerprint)
    {
        if (sequenceNumber < 1 || string.IsNullOrWhiteSpace(requestFingerprint))
        {
            throw new ArgumentException("The heartbeat identity is invalid.");
        }

        if (sequenceNumber == LastAcceptedSequence)
        {
            return string.Equals(
                    LastRequestFingerprint,
                    requestFingerprint,
                    StringComparison.Ordinal)
                ? new ReadingHeartbeatApplication(ReadingHeartbeatOutcome.Replayed)
                : new ReadingHeartbeatApplication(
                    ReadingHeartbeatOutcome.SequenceConflict,
                    ReadingSequenceConflictReasons.PayloadMismatch);
        }

        if (sequenceNumber < LastAcceptedSequence)
        {
            return new ReadingHeartbeatApplication(
                ReadingHeartbeatOutcome.SequenceConflict,
                ReadingSequenceConflictReasons.Stale);
        }

        return LastAcceptedSequence < long.MaxValue &&
               sequenceNumber == LastAcceptedSequence + 1
            ? new ReadingHeartbeatApplication(ReadingHeartbeatOutcome.Accepted)
            : new ReadingHeartbeatApplication(
                ReadingHeartbeatOutcome.SequenceConflict,
                ReadingSequenceConflictReasons.Gap);
    }

    public bool IsExpiredAt(DateTimeOffset timestamp)
    {
        EnsureUtc(timestamp, nameof(timestamp));
        return timestamp >= ExpiresAt;
    }

    public ReadingHeartbeatApplication ApplyHeartbeat(
        int currentPageOrder,
        decimal currentPageOffsetRatio,
        long sequenceNumber,
        string requestFingerprint,
        string currentManifestRevision,
        IReadOnlyList<int> currentReadyPageOrders,
        DateTimeOffset receivedAt)
    {
        if (currentPageOrder < 1 ||
            currentPageOffsetRatio is < 0 or > 1 ||
            sequenceNumber < 1 ||
            string.IsNullOrWhiteSpace(requestFingerprint) ||
            requestFingerprint.Length > 64)
        {
            throw new ArgumentException("The heartbeat payload is invalid.");
        }

        ArgumentNullException.ThrowIfNull(currentReadyPageOrders);
        EnsureManifest(currentManifestRevision, currentReadyPageOrders.Count);
        EnsureUtc(receivedAt, nameof(receivedAt));

        var sequence = ClassifySequence(sequenceNumber, requestFingerprint);
        if (sequence.Outcome != ReadingHeartbeatOutcome.Accepted)
        {
            return sequence;
        }

        if (IsExpiredAt(receivedAt))
        {
            return new ReadingHeartbeatApplication(ReadingHeartbeatOutcome.Expired);
        }

        var pageIndex = IndexOf(currentReadyPageOrders, currentPageOrder);
        var pageIsReady = pageIndex >= 0;

        var creditedDurationMs = LastActivityAt.HasValue && LastActivityWasReady && pageIsReady
            ? CalculateCreditedDuration(LastActivityAt.Value, receivedAt)
            : 0;
        ActiveDurationMs = checked(ActiveDurationMs + creditedDurationMs);
        LastActivityAt = receivedAt;
        LastActivityWasReady = pageIsReady;
        LastAcceptedSequence = sequenceNumber;
        LastRequestFingerprint = requestFingerprint;
        ManifestRevision = currentManifestRevision;
        ManifestPageCount = currentReadyPageOrders.Count;

        if (pageIsReady)
        {
            CurrentPageOrder = currentPageOrder;
            CurrentPageOffsetRatio = currentPageOffsetRatio;
            var candidateProgress = CalculateProgressBasisPoints(
                pageIndex,
                currentPageOffsetRatio,
                currentReadyPageOrders.Count);
            MaxProgressBasisPoints = Math.Max(
                MaxProgressBasisPoints,
                candidateProgress);
        }

        Version = checked(Version + 1);
        return new ReadingHeartbeatApplication(ReadingHeartbeatOutcome.Accepted);
    }

    public ReadingQualificationOutcome Qualify(DateTimeOffset qualifiedAt)
    {
        EnsureUtc(qualifiedAt, nameof(qualifiedAt));

        if (QualifiedAt.HasValue)
        {
            return ReadingQualificationOutcome.AlreadyQualified;
        }

        if (qualifiedAt >= ExpiresAt)
        {
            return ReadingQualificationOutcome.Expired;
        }

        if (MaxProgressBasisPoints < MinimumProgressBasisPoints)
        {
            return ReadingQualificationOutcome.ProgressThresholdNotMet;
        }

        if (ActiveDurationMs < MinimumActiveDurationMs)
        {
            return ReadingQualificationOutcome.ActiveDurationThresholdNotMet;
        }

        QualifiedAt = qualifiedAt;
        Status = ReadingSessionStatuses.Qualified;
        Version = checked(Version + 1);
        return ReadingQualificationOutcome.Qualified;
    }

    private static int IndexOf(IReadOnlyList<int> pageOrders, int pageOrder)
    {
        for (var index = 0; index < pageOrders.Count; index++)
        {
            if (pageOrders[index] == pageOrder)
            {
                return index;
            }
        }

        return -1;
    }

    private static int CalculateProgressBasisPoints(
        int zeroBasedPageIndex,
        decimal pageOffsetRatio,
        int pageCount)
    {
        var progress = ((zeroBasedPageIndex + pageOffsetRatio) / pageCount) * 10_000m;
        return decimal.ToInt32(decimal.Floor(progress));
    }

    private static long CalculateCreditedDuration(
        DateTimeOffset previousActivityAt,
        DateTimeOffset receivedAt)
    {
        var elapsedTicks = Math.Max(0, (receivedAt - previousActivityAt).Ticks);
        var elapsedMs = elapsedTicks / TimeSpan.TicksPerMillisecond;
        return Math.Min(elapsedMs, MaximumHeartbeatCreditMs);
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", parameterName);
        }
    }

    private static void EnsureManifest(string revision, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 64)
        {
            throw new ArgumentException("A valid manifest revision is required.", nameof(revision));
        }

        if (pageCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }
    }

    private static void EnsurePolicy(ReadingQualificationPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Revision) ||
            policy.Revision.Length > 32 ||
            policy.MinimumProgressBasisPoints is < 1 or > 10_000 ||
            policy.MinimumActiveDurationMs < 1)
        {
            throw new ArgumentException("The qualification policy is invalid.", nameof(policy));
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
