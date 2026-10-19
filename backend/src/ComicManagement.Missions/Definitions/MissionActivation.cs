namespace ComicManagement.Missions.Definitions;

public sealed class MissionActivation
{
    private MissionActivation()
    {
    }

    public Guid Id { get; private set; }

    public Guid MissionDefinitionId { get; private set; }

    public Guid MissionRevisionId { get; private set; }

    public long ActivationSequence { get; private set; }

    public DateTimeOffset ActivatedAt { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public static MissionActivation Create(
        Guid id,
        Guid missionDefinitionId,
        Guid missionRevisionId,
        long activationSequence,
        DateTimeOffset activatedAt)
    {
        if (id == Guid.Empty || missionDefinitionId == Guid.Empty ||
            missionRevisionId == Guid.Empty || activationSequence < 1 ||
            activatedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Mission activation is invalid.");
        }

        return new MissionActivation
        {
            Id = id,
            MissionDefinitionId = missionDefinitionId,
            MissionRevisionId = missionRevisionId,
            ActivationSequence = activationSequence,
            ActivatedAt = activatedAt,
        };
    }

    public void Close(DateTimeOffset deactivatedAt)
    {
        if (DeactivatedAt is not null || deactivatedAt.Offset != TimeSpan.Zero ||
            deactivatedAt < ActivatedAt)
        {
            throw new InvalidOperationException("Mission activation cannot be closed.");
        }

        DeactivatedAt = deactivatedAt;
    }

    public bool Contains(DateTimeOffset occurredAt) =>
        occurredAt.Offset == TimeSpan.Zero && occurredAt >= ActivatedAt &&
        (DeactivatedAt is null || occurredAt < DeactivatedAt);
}
