namespace ComicManagement.Missions.Definitions;

public static class MissionActionTypes
{
    public const string DailyLogin = "DailyLogin";
    public const string ChapterReadQualified = "ChapterReadQualified";
    public const string ComicFollowed = "ComicFollowed";
    public const string ChapterUnlocked = "ChapterUnlocked";
    public const string WalletTopUpCompleted = "WalletTopUpCompleted";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [DailyLogin, ChapterReadQualified, ComicFollowed, ChapterUnlocked, WalletTopUpCompleted],
        StringComparer.Ordinal);
}

public static class MissionCycleTypes
{
    public const string Fixed = "Fixed";
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Event = "Event";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Fixed, Daily, Weekly, Event],
        StringComparer.Ordinal);
}

public static class MissionDefinitionStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
}

public static class MissionRepeatPolicies
{
    public const string OncePerVietnamDate = "OncePerVietnamDate";
    public const string UniqueSubjectPerPeriod = "UniqueSubjectPerPeriod";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [OncePerVietnamDate, UniqueSubjectPerPeriod],
        StringComparer.Ordinal);
}

public sealed class MissionDefinition
{
    public const int MaximumCodeLength = 80;
    public const int MaximumNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;

    private MissionDefinition()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public string ActionType { get; private set; } = string.Empty;

    public string CycleType { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public Guid? ActiveRevisionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public long Version { get; private set; }

    public static MissionDefinition Create(
        Guid id,
        string code,
        string name,
        string description,
        string actionType,
        string cycleType,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty ||
            string.IsNullOrWhiteSpace(code) ||
            code.Length > MaximumCodeLength ||
            !string.Equals(code, code.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(name) ||
            name.Length > MaximumNameLength ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
            description is null ||
            description.Length > MaximumDescriptionLength ||
            !MissionActionTypes.All.Contains(actionType) ||
            !MissionCycleTypes.All.Contains(cycleType) ||
            createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Mission definition is invalid.");
        }

        return new MissionDefinition
        {
            Id = id,
            Code = code,
            Name = name,
            Description = description,
            ActionType = actionType,
            CycleType = cycleType,
            Status = MissionDefinitionStatuses.Draft,
            CreatedAt = createdAt,
            Version = 1,
        };
    }

    public void Activate(MissionRevision revision, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(revision);

        if (revision.MissionId != Id)
        {
            throw new ArgumentException(
                "Active revision must belong to the mission definition.",
                nameof(revision));
        }

        var expectedRepeatPolicy = ActionType == MissionActionTypes.DailyLogin
            ? MissionRepeatPolicies.OncePerVietnamDate
            : MissionRepeatPolicies.UniqueSubjectPerPeriod;
        if (!string.Equals(
                revision.RepeatPolicy,
                expectedRepeatPolicy,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Revision repeat policy does not match the mission action semantics.",
                nameof(revision));
        }

        if (Version != expectedVersion)
        {
            throw new MissionDefinitionConcurrencyException();
        }

        ActiveRevisionId = revision.Id;
        Status = MissionDefinitionStatuses.Active;
        Version++;
    }

    public void Deactivate(long expectedVersion)
    {
        if (Version != expectedVersion)
        {
            throw new MissionDefinitionConcurrencyException();
        }

        ActiveRevisionId = null;
        Status = MissionDefinitionStatuses.Draft;
        Version++;
    }
}

public sealed class MissionDefinitionConcurrencyException : InvalidOperationException;
