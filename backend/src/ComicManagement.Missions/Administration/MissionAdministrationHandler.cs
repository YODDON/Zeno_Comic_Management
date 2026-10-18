using System.Text.Json;
using ComicManagement.Missions.Definitions;

namespace ComicManagement.Missions.Administration;

public enum MissionAdministrationOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
    CodeConflict = 3,
    ConcurrencyConflict = 4,
    StateConflict = 5,
}

public sealed record MissionRevisionState(
    Guid Id,
    Guid MissionId,
    int RevisionNumber,
    int TargetCount,
    long RewardBonusCoins,
    string RepeatPolicy,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveUntil,
    JsonElement ConfigurationSnapshot);

public sealed record MissionDefinitionState(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string ActionType,
    string CycleType,
    string Status,
    Guid? ActiveRevisionId,
    DateTimeOffset CreatedAt,
    long Version,
    IReadOnlyList<MissionRevisionState> Revisions);

public sealed record MissionAdministrationResult(
    MissionAdministrationOutcome Outcome,
    MissionDefinitionState? Definition = null,
    MissionRevisionState? Revision = null,
    IReadOnlyList<MissionDefinitionState>? Definitions = null);

public sealed record CreateMissionDefinitionStoreRequest(
    Guid DefinitionId,
    Guid ActorId,
    string Code,
    string Name,
    string Description,
    string ActionType,
    string CycleType,
    string CorrelationId);

public sealed record AddMissionRevisionStoreRequest(
    Guid RevisionId,
    Guid MissionId,
    Guid ActorId,
    int TargetCount,
    long RewardBonusCoins,
    string RepeatPolicy,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveUntil,
    string ConfigurationSnapshot,
    string CorrelationId);

public sealed record ChangeMissionActivationStoreRequest(
    Guid MissionId,
    Guid? RevisionId,
    Guid ActorId,
    long ExpectedVersion,
    string CorrelationId);

public interface IMissionAdministrationStore
{
    Task<MissionAdministrationResult> ListAsync(
        CancellationToken cancellationToken = default);

    Task<MissionAdministrationResult> GetAsync(
        Guid missionId,
        CancellationToken cancellationToken = default);

    Task<MissionAdministrationResult> CreateAsync(
        CreateMissionDefinitionStoreRequest request,
        CancellationToken cancellationToken = default);

    Task<MissionAdministrationResult> AddRevisionAsync(
        AddMissionRevisionStoreRequest request,
        CancellationToken cancellationToken = default);

    Task<MissionAdministrationResult> ChangeActivationAsync(
        ChangeMissionActivationStoreRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class MissionAdministrationHandler(IMissionAdministrationStore store)
{
    public Task<MissionAdministrationResult> ListAsync(
        CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    public Task<MissionAdministrationResult> GetAsync(
        Guid missionId,
        CancellationToken cancellationToken = default) =>
        missionId == Guid.Empty
            ? ValidationFailed()
            : store.GetAsync(missionId, cancellationToken);

    public Task<MissionAdministrationResult> CreateAsync(
        Guid actorId,
        string? code,
        string? name,
        string? description,
        string? actionType,
        string? cycleType,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty ||
            string.IsNullOrWhiteSpace(code) ||
            code.Length > MissionDefinition.MaximumCodeLength ||
            !string.Equals(code, code.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(name) ||
            name.Length > MissionDefinition.MaximumNameLength ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
            description is null ||
            description.Length > MissionDefinition.MaximumDescriptionLength ||
            actionType is null ||
            !MissionActionTypes.All.Contains(actionType) ||
            cycleType is null ||
            !MissionCycleTypes.All.Contains(cycleType) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            return ValidationFailed();
        }

        return store.CreateAsync(
            new CreateMissionDefinitionStoreRequest(
                Guid.NewGuid(),
                actorId,
                code,
                name,
                description,
                actionType,
                cycleType,
                correlationId),
            cancellationToken);
    }

    public Task<MissionAdministrationResult> AddRevisionAsync(
        Guid actorId,
        Guid missionId,
        int targetCount,
        long rewardBonusCoins,
        string? repeatPolicy,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveUntil,
        string? configurationSnapshot,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty ||
            missionId == Guid.Empty ||
            targetCount < 1 ||
            rewardBonusCoins is < 1 or > 300 ||
            repeatPolicy is null ||
            !MissionRepeatPolicies.All.Contains(repeatPolicy) ||
            effectiveFrom == default ||
            effectiveFrom.Offset != TimeSpan.Zero ||
            effectiveUntil is { Offset: var offset } && offset != TimeSpan.Zero ||
            effectiveUntil is not null && effectiveUntil <= effectiveFrom ||
            !IsJsonObject(configurationSnapshot) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            return ValidationFailed();
        }

        return store.AddRevisionAsync(
            new AddMissionRevisionStoreRequest(
                Guid.NewGuid(),
                missionId,
                actorId,
                targetCount,
                rewardBonusCoins,
                repeatPolicy,
                effectiveFrom,
                effectiveUntil,
                configurationSnapshot!,
                correlationId),
            cancellationToken);
    }

    public Task<MissionAdministrationResult> ActivateAsync(
        Guid actorId,
        Guid missionId,
        Guid revisionId,
        long expectedVersion,
        string? correlationId,
        CancellationToken cancellationToken = default) =>
        actorId == Guid.Empty ||
        missionId == Guid.Empty ||
        revisionId == Guid.Empty ||
        expectedVersion < 1 ||
        string.IsNullOrWhiteSpace(correlationId)
            ? ValidationFailed()
            : store.ChangeActivationAsync(
                new ChangeMissionActivationStoreRequest(
                    missionId,
                    revisionId,
                    actorId,
                    expectedVersion,
                    correlationId),
                cancellationToken);

    public Task<MissionAdministrationResult> DeactivateAsync(
        Guid actorId,
        Guid missionId,
        long expectedVersion,
        string? correlationId,
        CancellationToken cancellationToken = default) =>
        actorId == Guid.Empty ||
        missionId == Guid.Empty ||
        expectedVersion < 1 ||
        string.IsNullOrWhiteSpace(correlationId)
            ? ValidationFailed()
            : store.ChangeActivationAsync(
                new ChangeMissionActivationStoreRequest(
                    missionId,
                    null,
                    actorId,
                    expectedVersion,
                    correlationId),
                cancellationToken);

    private static bool IsJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Task<MissionAdministrationResult> ValidationFailed() =>
        Task.FromResult(new MissionAdministrationResult(
            MissionAdministrationOutcome.ValidationFailed));
}
