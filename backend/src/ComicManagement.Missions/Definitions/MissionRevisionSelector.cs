namespace ComicManagement.Missions.Definitions;

public static class MissionRevisionSelector
{
    public static MissionRevision? Select(
        MissionDefinition definition,
        IEnumerable<MissionRevision> revisions,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(revisions);

        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Mission timestamps must use the UTC offset.",
                nameof(occurredAt));
        }

        if (!string.Equals(
                definition.Status,
                MissionDefinitionStatuses.Active,
                StringComparison.Ordinal) ||
            definition.ActiveRevisionId is null)
        {
            return null;
        }

        var activeRevision = revisions.SingleOrDefault(
            revision => revision.Id == definition.ActiveRevisionId.Value);
        if (activeRevision is null || activeRevision.MissionId != definition.Id)
        {
            throw new InvalidOperationException(
                "The active mission revision is missing or belongs to another definition.");
        }

        var expectedRepeatPolicy = definition.ActionType == MissionActionTypes.DailyLogin
            ? MissionRepeatPolicies.OncePerVietnamDate
            : MissionRepeatPolicies.UniqueSubjectPerPeriod;
        if (!string.Equals(
                activeRevision.RepeatPolicy,
                expectedRepeatPolicy,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active mission revision is inconsistent with its action type.");
        }

        return activeRevision.AppliesAt(occurredAt) ? activeRevision : null;
    }
}
