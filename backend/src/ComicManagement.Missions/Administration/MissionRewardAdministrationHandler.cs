using ComicManagement.Missions.Rewards;
using ComicManagement.Shared.Pagination;

namespace ComicManagement.Missions.Administration;

public enum MissionRewardAdministrationOutcome
{
    Succeeded,
    ValidationFailed,
}

public sealed record AdminMissionRewardItem(
    Guid RewardGrantId,
    Guid UserId,
    string UserDisplayName,
    string? UserEmail,
    Guid MissionId,
    Guid RevisionId,
    string MissionName,
    string Outcome,
    long RewardBonusCoins,
    long GrantedBonusCoins,
    DateOnly LocalDate,
    DateTimeOffset DecidedAt);

public sealed record AdminMissionRewardPage(
    IReadOnlyList<AdminMissionRewardItem> Items,
    int Page,
    int PageSize,
    long TotalItems,
    int TotalPages);

public sealed record MissionRewardAdministrationResult(
    MissionRewardAdministrationOutcome Outcome,
    AdminMissionRewardPage? Page = null);

public interface IMissionRewardAdministrationStore
{
    Task<AdminMissionRewardPage> ListAsync(
        string? outcome,
        OffsetPage page,
        CancellationToken cancellationToken = default);
}

public sealed class MissionRewardAdministrationHandler(IMissionRewardAdministrationStore store)
{
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 100;

    public async Task<MissionRewardAdministrationResult> ListAsync(
        string? outcome,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim();
        if (outcome is not null && outcome is not (MissionRewardOutcomes.Granted or MissionRewardOutcomes.DeniedDailyCap) ||
            !OffsetPage.TryCreate(page, pageSize, MaximumPageSize, out var requestedPage))
        {
            return new MissionRewardAdministrationResult(
                MissionRewardAdministrationOutcome.ValidationFailed);
        }

        return new MissionRewardAdministrationResult(
            MissionRewardAdministrationOutcome.Succeeded,
            await store.ListAsync(outcome, requestedPage, cancellationToken));
    }
}
