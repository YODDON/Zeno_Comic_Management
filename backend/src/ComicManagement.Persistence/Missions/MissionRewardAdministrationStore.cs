using ComicManagement.Missions.Administration;
using ComicManagement.Missions.Rewards;
using ComicManagement.Shared.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class MissionRewardAdministrationStore(AppDbContext dbContext)
    : IMissionRewardAdministrationStore
{
    public async Task<AdminMissionRewardPage> ListAsync(
        string? outcome,
        OffsetPage page,
        CancellationToken cancellationToken = default)
    {
        var query =
            from reward in dbContext.MissionRewardGrants.AsNoTracking()
            join progress in dbContext.MissionProgress.AsNoTracking() on reward.ProgressId equals progress.Id
            join revision in dbContext.MissionRevisions.AsNoTracking() on progress.MissionRevisionId equals revision.Id
            join definition in dbContext.MissionDefinitions.AsNoTracking() on revision.MissionId equals definition.Id
            join user in dbContext.Users.AsNoTracking() on reward.UserId equals user.Id
            select new { Reward = reward, Revision = revision, Definition = definition, User = user };
        if (outcome is not null)
        {
            query = query.Where(item => item.Reward.Outcome == outcome);
        }

        var totalItems = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.Reward.DecidedAt)
            .ThenByDescending(item => item.Reward.Id)
            .Skip(page.Offset)
            .Take(page.PageSize)
            .Select(item => new AdminMissionRewardItem(
                item.Reward.Id,
                item.Reward.UserId,
                item.User.DisplayName,
                item.User.Email,
                item.Definition.Id,
                item.Revision.Id,
                item.Definition.Name,
                item.Reward.Outcome,
                item.Reward.BonusCoins,
                item.Reward.Outcome == MissionRewardOutcomes.Granted ? item.Reward.BonusCoins : 0,
                item.Reward.LocalDate,
                item.Reward.DecidedAt))
            .ToListAsync(cancellationToken);

        return new AdminMissionRewardPage(
            items,
            page.Page,
            page.PageSize,
            totalItems,
            page.TotalPages(totalItems));
    }
}
