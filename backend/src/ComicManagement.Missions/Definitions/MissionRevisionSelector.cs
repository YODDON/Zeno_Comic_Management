namespace ComicManagement.Missions.Definitions;

public static class MissionRevisionSelector
{
    public static MissionRevision? Select(
        IEnumerable<MissionRevision> revisions,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(revisions);

        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Mission timestamps must use the UTC offset.",
                nameof(occurredAt));
        }

        var applicable = revisions
            .Where(revision => revision.AppliesAt(occurredAt))
            .OrderByDescending(revision => revision.RevisionNumber)
            .Take(2)
            .ToArray();

        return applicable.Length switch
        {
            0 => null,
            1 => applicable[0],
            _ => throw new InvalidOperationException(
                "Mission revision effective windows overlap."),
        };
    }
}
