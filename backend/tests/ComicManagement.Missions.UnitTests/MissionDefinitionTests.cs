using ComicManagement.Missions.Definitions;
using Xunit;

namespace ComicManagement.Missions.UnitTests;

public sealed class MissionDefinitionTests
{
    [Fact]
    public void Activation_enforces_action_repeat_semantics_and_revision_ownership()
    {
        var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var definition = MissionDefinition.Create(
            Guid.NewGuid(),
            "daily-login",
            "Daily login",
            string.Empty,
            MissionActionTypes.DailyLogin,
            MissionCycleTypes.Weekly,
            now);
        var wrongPolicy = MissionRevision.Create(
            Guid.NewGuid(),
            definition.Id,
            1,
            7,
            30,
            MissionRepeatPolicies.UniqueSubjectPerPeriod,
            now,
            null,
            "{}");
        var wrongOwner = MissionRevision.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            7,
            30,
            MissionRepeatPolicies.OncePerVietnamDate,
            now,
            null,
            "{}");

        Assert.Throws<ArgumentException>(() => definition.Activate(wrongPolicy, 1));
        Assert.Throws<ArgumentException>(() => definition.Activate(wrongOwner, 1));
    }

    [Fact]
    public void Revision_rejects_reward_above_daily_cap()
    {
        Assert.Throws<ArgumentException>(() => MissionRevision.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            1,
            301,
            MissionRepeatPolicies.OncePerVietnamDate,
            DateTimeOffset.UtcNow,
            null,
            "{}"));
    }

    [Fact]
    public void Selector_uses_occurred_at_and_not_processing_order()
    {
        var missionId = Guid.NewGuid();
        var boundary = new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.Zero);
        var first = MissionRevision.Create(
            Guid.NewGuid(), missionId, 1, 1, 20,
            MissionRepeatPolicies.OncePerVietnamDate,
            boundary.AddDays(-7), boundary, "{}");
        var second = MissionRevision.Create(
            Guid.NewGuid(), missionId, 2, 2, 30,
            MissionRepeatPolicies.OncePerVietnamDate,
            boundary, null, "{}");

        Assert.Same(first, MissionRevisionSelector.Select(
            [second, first], boundary.AddTicks(-1)));
        Assert.Same(second, MissionRevisionSelector.Select(
            [first, second], boundary));
    }

    [Fact]
    public void Selector_fails_closed_for_overlapping_effective_windows()
    {
        var missionId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var revisions = new[]
        {
            MissionRevision.Create(
                Guid.NewGuid(), missionId, 1, 1, 20,
                MissionRepeatPolicies.OncePerVietnamDate, now.AddDays(-1), null, "{}"),
            MissionRevision.Create(
                Guid.NewGuid(), missionId, 2, 1, 20,
                MissionRepeatPolicies.OncePerVietnamDate, now.AddHours(-1), null, "{}"),
        };

        Assert.Throws<InvalidOperationException>(() =>
            MissionRevisionSelector.Select(revisions, now));
    }
}
