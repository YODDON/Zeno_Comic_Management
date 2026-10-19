namespace ComicManagement.Missions.Administration;

public interface IMissionActivationCriticalSectionObserver
{
    ValueTask DefinitionLockedAsync(
        Guid missionDefinitionId,
        CancellationToken cancellationToken);
}

public sealed class NoOpMissionActivationCriticalSectionObserver :
    IMissionActivationCriticalSectionObserver
{
    public ValueTask DefinitionLockedAsync(
        Guid missionDefinitionId,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
