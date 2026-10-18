namespace ComicManagement.Missions.Periods;

using ComicManagement.Missions.Definitions;

public sealed record MissionPeriod(
    string Key,
    DateOnly LocalDate,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt);

public sealed class MissionPeriodResolver
{
    public const string VietnamTimeZoneId = "Asia/Ho_Chi_Minh";

    private readonly TimeZoneInfo _timeZone;

    public MissionPeriodResolver()
        : this(FindVietnamTimeZone())
    {
    }

    internal MissionPeriodResolver(TimeZoneInfo timeZone)
    {
        _timeZone = timeZone;
    }

    public DateOnly GetLocalDate(DateTimeOffset occurredAt)
    {
        EnsureUtc(occurredAt);
        return GetLocalDate(occurredAt, _timeZone);
    }

    public static DateOnly GetVietnamLocalDate(DateTimeOffset occurredAt)
    {
        EnsureUtc(occurredAt);
        return GetLocalDate(occurredAt, FindVietnamTimeZone());
    }

    public MissionPeriod Resolve(
        string cycleType,
        DateTimeOffset occurredAt,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveUntil)
    {
        EnsureUtc(occurredAt);
        EnsureUtc(effectiveFrom);
        if (effectiveUntil is not null)
        {
            EnsureUtc(effectiveUntil.Value);
        }

        if (occurredAt < effectiveFrom ||
            (effectiveUntil is not null && occurredAt >= effectiveUntil))
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredAt),
                "The action did not occur in the revision effective window.");
        }

        var localDate = GetLocalDate(occurredAt);
        return cycleType switch
        {
            MissionCycleTypes.Daily => FromLocalDates(
                $"daily:{localDate:yyyy-MM-dd}",
                localDate,
                localDate,
                localDate.AddDays(1)),
            MissionCycleTypes.Weekly => ResolveWeekly(localDate),
            MissionCycleTypes.Fixed => new MissionPeriod(
                "fixed",
                localDate,
                effectiveFrom,
                effectiveUntil),
            MissionCycleTypes.Event => new MissionPeriod(
                "event",
                localDate,
                effectiveFrom,
                effectiveUntil),
            _ => throw new ArgumentOutOfRangeException(
                nameof(cycleType),
                "Unsupported mission cycle type."),
        };
    }

    private MissionPeriod ResolveWeekly(DateOnly localDate)
    {
        var daysSinceMonday = ((int)localDate.DayOfWeek + 6) % 7;
        var monday = localDate.AddDays(-daysSinceMonday);
        return FromLocalDates(
            $"weekly:{monday:yyyy-MM-dd}",
            localDate,
            monday,
            monday.AddDays(7));
    }

    private MissionPeriod FromLocalDates(
        string key,
        DateOnly actionLocalDate,
        DateOnly startsOn,
        DateOnly endsOn) => new(
            key,
            actionLocalDate,
            ToUtc(startsOn),
            ToUtc(endsOn));

    private DateTimeOffset ToUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _timeZone), TimeSpan.Zero);
    }

    private static TimeZoneInfo FindVietnamTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(VietnamTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        }
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Mission timestamps must use the UTC offset.");
        }
    }

    private static DateOnly GetLocalDate(DateTimeOffset occurredAt, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurredAt, timeZone).DateTime);
}
