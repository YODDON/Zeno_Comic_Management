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
    public void Selector_uses_only_the_authoritative_active_revision()
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

        var definition = MissionDefinition.Create(
            missionId, "active-selector", "Active selector", string.Empty,
            MissionActionTypes.DailyLogin, MissionCycleTypes.Daily, boundary.AddDays(-8));
        definition.Activate(first, 1);

        Assert.Same(first, MissionRevisionSelector.Select(
            definition, [second, first], boundary.AddTicks(-1)));
        Assert.Null(MissionRevisionSelector.Select(
            definition, [first, second], boundary));

        definition.Activate(second, 2);
        Assert.Same(second, MissionRevisionSelector.Select(
            definition, [first, second], boundary));
    }

    [Fact]
    public void Selector_ignores_overlapping_unactivated_revisions()
    {
        var missionId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var first = MissionRevision.Create(
            Guid.NewGuid(), missionId, 1, 1, 20,
            MissionRepeatPolicies.OncePerVietnamDate, now.AddDays(-1), null, "{}");
        var draft = MissionRevision.Create(
            Guid.NewGuid(), missionId, 2, 1, 20,
            MissionRepeatPolicies.OncePerVietnamDate, now.AddHours(-1), null, "{}");
        var definition = MissionDefinition.Create(
            missionId, "overlap-selector", "Overlap selector", string.Empty,
            MissionActionTypes.DailyLogin, MissionCycleTypes.Daily, now.AddDays(-2));
        definition.Activate(first, 1);

        Assert.Same(first, MissionRevisionSelector.Select(definition, [first, draft], now));
    }

    [Fact]
    public void Selector_does_not_fallback_when_active_revision_is_outside_its_window()
    {
        var missionId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var expired = MissionRevision.Create(
            Guid.NewGuid(), missionId, 1, 1, 20,
            MissionRepeatPolicies.OncePerVietnamDate,
            now.AddDays(-2), now.AddDays(-1), "{}");
        var fallbackCandidate = MissionRevision.Create(
            Guid.NewGuid(), missionId, 2, 1, 20,
            MissionRepeatPolicies.OncePerVietnamDate,
            now.AddDays(-2), null, "{}");
        var definition = MissionDefinition.Create(
            missionId, "no-fallback", "No fallback", string.Empty,
            MissionActionTypes.DailyLogin, MissionCycleTypes.Daily, now.AddDays(-3));
        definition.Activate(expired, 1);

        Assert.Null(MissionRevisionSelector.Select(
            definition,
            [expired, fallbackCandidate],
            now));
    }

    [Fact]
    public void Definition_rejects_null_description_as_validation_error()
    {
        Assert.Throws<ArgumentException>(() => MissionDefinition.Create(
            Guid.NewGuid(),
            "null-description",
            "Null description",
            null!,
            MissionActionTypes.DailyLogin,
            MissionCycleTypes.Daily,
            DateTimeOffset.UtcNow));
    }
}
