using System.Text.Json;

namespace ComicManagement.Missions.Definitions;

public sealed class MissionRevision
{
    public const int MaximumRepeatPolicyLength = 80;

    private MissionRevision()
    {
    }

    public Guid Id { get; private set; }

    public Guid MissionId { get; private set; }

    public int RevisionNumber { get; private set; }

    public int TargetCount { get; private set; }

    public long RewardBonusCoins { get; private set; }

    public string RepeatPolicy { get; private set; } = string.Empty;

    public DateTimeOffset EffectiveFrom { get; private set; }

    public DateTimeOffset? EffectiveUntil { get; private set; }

    public string ConfigurationSnapshot { get; private set; } = "{}";

    public static MissionRevision Create(
        Guid id,
        Guid missionId,
        int revisionNumber,
        int targetCount,
        long rewardBonusCoins,
        string repeatPolicy,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveUntil,
        string configurationSnapshot)
    {
        if (id == Guid.Empty ||
            missionId == Guid.Empty ||
            revisionNumber < 1 ||
            targetCount < 1 ||
            rewardBonusCoins is < 1 or > 300 ||
            !MissionRepeatPolicies.All.Contains(repeatPolicy) ||
            effectiveFrom.Offset != TimeSpan.Zero ||
            (effectiveUntil is not null && effectiveUntil.Value.Offset != TimeSpan.Zero) ||
            (effectiveUntil is not null && effectiveUntil <= effectiveFrom) ||
            !IsJsonObject(configurationSnapshot))
        {
            throw new ArgumentException("Mission revision is invalid.");
        }

        return new MissionRevision
        {
            Id = id,
            MissionId = missionId,
            RevisionNumber = revisionNumber,
            TargetCount = targetCount,
            RewardBonusCoins = rewardBonusCoins,
            RepeatPolicy = repeatPolicy,
            EffectiveFrom = effectiveFrom,
            EffectiveUntil = effectiveUntil,
            ConfigurationSnapshot = configurationSnapshot,
        };
    }

    public bool AppliesAt(DateTimeOffset occurredAt) =>
        occurredAt.Offset == TimeSpan.Zero &&
        occurredAt >= EffectiveFrom &&
        (EffectiveUntil is null || occurredAt < EffectiveUntil);

    private static bool IsJsonObject(string value)
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
}
