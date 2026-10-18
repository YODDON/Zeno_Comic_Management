using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Periods;
using Xunit;

namespace ComicManagement.Missions.UnitTests;

public sealed class MissionPeriodResolverTests
{
    private readonly MissionPeriodResolver _resolver = new();

    [Fact]
    public void Daily_period_uses_vietnam_local_date_not_utc_date()
    {
        var occurredAt = new DateTimeOffset(2026, 10, 18, 18, 30, 0, TimeSpan.Zero);

        var period = _resolver.Resolve(
            MissionCycleTypes.Daily,
            occurredAt,
            occurredAt.AddDays(-1),
            null);

        Assert.Equal("daily:2026-10-19", period.Key);
        Assert.Equal(new DateOnly(2026, 10, 19), period.LocalDate);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 18, 17, 0, 0, TimeSpan.Zero),
            period.StartsAt);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.Zero),
            period.EndsAt);
    }

    [Fact]
    public void Weekly_period_changes_exactly_at_monday_midnight_in_vietnam()
    {
        var beforeMonday = new DateTimeOffset(2026, 10, 18, 16, 59, 59, TimeSpan.Zero);
        var monday = beforeMonday.AddSeconds(1);
        var effectiveFrom = beforeMonday.AddDays(-30);

        var previous = _resolver.Resolve(
            MissionCycleTypes.Weekly,
            beforeMonday,
            effectiveFrom,
            null);
        var current = _resolver.Resolve(
            MissionCycleTypes.Weekly,
            monday,
            effectiveFrom,
            null);

        Assert.Equal("weekly:2026-10-12", previous.Key);
        Assert.Equal("weekly:2026-10-19", current.Key);
        Assert.Equal(monday, current.StartsAt);
        Assert.Equal(monday.AddDays(7), current.EndsAt);
    }

    [Theory]
    [InlineData("Fixed", "fixed")]
    [InlineData("Event", "event")]
    public void Fixed_and_event_periods_use_revision_effective_window(
        string cycleType,
        string expectedKey)
    {
        var startsAt = new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero);
        var endsAt = startsAt.AddDays(10);

        var period = _resolver.Resolve(
            cycleType,
            startsAt.AddDays(5),
            startsAt,
            endsAt);

        Assert.Equal(expectedKey, period.Key);
        Assert.Equal(startsAt, period.StartsAt);
        Assert.Equal(endsAt, period.EndsAt);
    }

    [Fact]
    public void Processing_time_cannot_change_action_local_date()
    {
        var occurredAt = new DateTimeOffset(2026, 10, 18, 16, 59, 59, TimeSpan.Zero);
        var processedAt = occurredAt.AddDays(2);

        var actionDate = _resolver.GetLocalDate(occurredAt);
        var processingDate = _resolver.GetLocalDate(processedAt);

        Assert.Equal(new DateOnly(2026, 10, 18), actionDate);
        Assert.Equal(new DateOnly(2026, 10, 20), processingDate);
        Assert.NotEqual(processingDate, actionDate);
    }
}
