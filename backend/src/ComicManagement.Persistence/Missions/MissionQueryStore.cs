using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Periods;
using ComicManagement.Missions.Progress;
using ComicManagement.Missions.Queries;
using ComicManagement.Missions.Rewards;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class MissionQueryStore(
    AppDbContext dbContext,
    MissionPeriodResolver periodResolver) : IMissionQueryStore
{
    public async Task<IReadOnlyList<MissionState>> ListCurrentAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var definitions = await dbContext.MissionDefinitions.AsNoTracking()
            .Where(item => item.Status == MissionDefinitionStatuses.Active &&
                item.ActiveRevisionId != null)
            .OrderBy(item => item.Code)
            .ToArrayAsync(cancellationToken);
        var revisionIds = definitions.Select(item => item.ActiveRevisionId!.Value).ToArray();
        var revisions = await dbContext.MissionRevisions.AsNoTracking()
            .Where(item => revisionIds.Contains(item.Id) && item.EffectiveFrom <= now &&
                (item.EffectiveUntil == null || now < item.EffectiveUntil))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var eligible = definitions
            .Where(definition =>
                revisions.TryGetValue(definition.ActiveRevisionId!.Value, out var revision) &&
                revision.MissionId == definition.Id)
            .Select(definition =>
            {
                var revision = revisions[definition.ActiveRevisionId!.Value];
                return new
                {
                    Definition = definition,
                    Revision = revision,
                    Period = periodResolver.Resolve(
                        definition.CycleType,
                        now,
                        revision.EffectiveFrom,
                        revision.EffectiveUntil),
                };
            })
            .ToArray();
        var eligibleRevisionIds = eligible.Select(item => item.Revision.Id).ToArray();
        var progress = await dbContext.MissionProgress.AsNoTracking()
            .Where(item => item.UserId == userId &&
                eligibleRevisionIds.Contains(item.MissionRevisionId))
            .ToArrayAsync(cancellationToken);
        var progressByRevisionAndPeriod = progress.ToDictionary(
            item => (item.MissionRevisionId, item.PeriodKey));
        var progressIds = progress.Select(item => item.Id).ToArray();
        var rewardsByProgressId = await dbContext.MissionRewardGrants.AsNoTracking()
            .Where(item => progressIds.Contains(item.ProgressId))
            .ToDictionaryAsync(item => item.ProgressId, cancellationToken);
        var result = new List<MissionState>(eligible.Length);

        foreach (var item in eligible)
        {
            var definition = item.Definition;
            var revision = item.Revision;
            progressByRevisionAndPeriod.TryGetValue(
                (revision.Id, item.Period.Key),
                out var currentProgress);
            MissionRewardGrant? reward = currentProgress is not null &&
                rewardsByProgressId.TryGetValue(currentProgress.Id, out var currentReward)
                    ? currentReward
                    : null;

            result.Add(new MissionState(
                definition.Id,
                revision.Id,
                definition.Code,
                definition.Name,
                definition.Description,
                definition.ActionType,
                definition.CycleType,
                item.Period.Key,
                revision.TargetCount,
                currentProgress?.ProgressCount ?? 0,
                revision.RewardBonusCoins,
                currentProgress?.Status ?? MissionProgressStatuses.InProgress,
                reward?.Outcome ?? "Pending",
                revision.EffectiveFrom,
                revision.EffectiveUntil));
        }

        return result;
    }

    public async Task<MissionDailyLimitState> GetDailyLimitAsync(
        Guid userId,
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        var granted = await dbContext.MissionDailyCounters.AsNoTracking()
            .Where(item => item.UserId == userId && item.LocalDate == localDate)
            .Select(item => (long?)item.GrantedTotal)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        return new MissionDailyLimitState(
            localDate,
            MissionDailyCounter.MaximumDailyBonusCoins,
            granted,
            MissionDailyCounter.MaximumDailyBonusCoins - granted);
    }

    public async Task<IReadOnlyList<MissionRewardState>> ListRewardsAsync(
        Guid userId,
        MissionRewardCursor? cursor,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query =
            from reward in dbContext.MissionRewardGrants.AsNoTracking()
            join progress in dbContext.MissionProgress.AsNoTracking()
                on reward.ProgressId equals progress.Id
            join revision in dbContext.MissionRevisions.AsNoTracking()
                on progress.MissionRevisionId equals revision.Id
            join definition in dbContext.MissionDefinitions.AsNoTracking()
                on revision.MissionId equals definition.Id
            where reward.UserId == userId
            select new { Reward = reward, Revision = revision, Definition = definition };

        if (cursor is not null)
        {
            query = query.Where(row => row.Reward.DecidedAt < cursor.DecidedAt ||
                (row.Reward.DecidedAt == cursor.DecidedAt &&
                 row.Reward.Id.CompareTo(cursor.RewardGrantId) < 0));
        }

        return await query
            .OrderByDescending(row => row.Reward.DecidedAt)
            .ThenByDescending(row => row.Reward.Id)
            .Take(take)
            .Select(row => new MissionRewardState(
                row.Reward.Id,
                row.Definition.Id,
                row.Revision.Id,
                row.Definition.Name,
                row.Reward.Outcome,
                row.Reward.BonusCoins,
                row.Reward.Outcome == MissionRewardOutcomes.Granted
                    ? row.Reward.BonusCoins
                    : 0,
                row.Reward.LocalDate,
                row.Reward.DecidedAt))
            .ToListAsync(cancellationToken);
    }
}
