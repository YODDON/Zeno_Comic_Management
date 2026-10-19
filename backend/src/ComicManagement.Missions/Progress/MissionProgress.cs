namespace ComicManagement.Missions.Progress;

public static class MissionProgressStatuses
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
}

public sealed class MissionProgress
{
    public const int MaximumPeriodKeyLength = 80;

    private MissionProgress()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid MissionRevisionId { get; private set; }
    public string PeriodKey { get; private set; } = string.Empty;
    public int ProgressCount { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Version { get; private set; }

    public static MissionProgress Create(
        Guid id,
        Guid userId,
        Guid missionRevisionId,
        string periodKey)
    {
        if (id == Guid.Empty || userId == Guid.Empty || missionRevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(periodKey) || periodKey.Length > MaximumPeriodKeyLength)
        {
            throw new ArgumentException("Mission progress is invalid.");
        }

        return new MissionProgress
        {
            Id = id,
            UserId = userId,
            MissionRevisionId = missionRevisionId,
            PeriodKey = periodKey,
            Status = MissionProgressStatuses.InProgress,
            Version = 1,
        };
    }

    public bool Increment(int targetCount, DateTimeOffset occurredAt)
    {
        if (targetCount < 1 || occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Mission progress increment is invalid.");
        }

        if (Status == MissionProgressStatuses.Completed)
        {
            return false;
        }

        ProgressCount = Math.Min(checked(ProgressCount + 1), targetCount);
        Version = checked(Version + 1);
        if (ProgressCount < targetCount)
        {
            return false;
        }

        Status = MissionProgressStatuses.Completed;
        CompletedAt = occurredAt;
        return true;
    }
}

public sealed class ProgressAction
{
    public const int MaximumDedupKeyLength = 160;

    private ProgressAction()
    {
    }

    public Guid MissionProgressId { get; private set; }
    public Guid SourceEventId { get; private set; }
    public Guid? SubjectId { get; private set; }
    public string DedupKey { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }

    public static ProgressAction Create(
        Guid missionProgressId,
        Guid sourceEventId,
        Guid? subjectId,
        string dedupKey,
        DateTimeOffset occurredAt)
    {
        if (missionProgressId == Guid.Empty || sourceEventId == Guid.Empty ||
            subjectId == Guid.Empty || string.IsNullOrWhiteSpace(dedupKey) ||
            dedupKey.Length > MaximumDedupKeyLength || occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Mission progress action is invalid.");
        }

        return new ProgressAction
        {
            MissionProgressId = missionProgressId,
            SourceEventId = sourceEventId,
            SubjectId = subjectId,
            DedupKey = dedupKey,
            OccurredAt = occurredAt,
        };
    }
}
