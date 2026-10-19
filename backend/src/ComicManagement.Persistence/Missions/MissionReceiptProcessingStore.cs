using System.Data;
using ComicManagement.Billing.Events;
using ComicManagement.Identity.Events;
using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Periods;
using ComicManagement.Missions.Processing;
using ComicManagement.Missions.Progress;
using ComicManagement.Missions.Receipts;
using ComicManagement.Missions.Rewards;
using ComicManagement.Reading.Events;
using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Spending;
using Microsoft.EntityFrameworkCore;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence;

internal sealed class MissionReceiptProcessingStore(
    AppDbContext dbContext,
    WalletSpendEngine walletEngine,
    MissionPeriodResolver periodResolver,
    IOutboxWriter outboxWriter,
    IMissionReceiptCriticalSectionObserver criticalSectionObserver) :
    IMissionReceiptProcessingStore
{
    public async Task<MissionReceiptProcessingOutcome> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var candidate = await dbContext.ActionReceipts
            .AsNoTracking()
            .Where(receipt => receipt.ProcessingStatus == ActionReceiptStatuses.Pending)
            .OrderBy(receipt => receipt.ReceivedAt)
            .ThenBy(receipt => receipt.Id)
            .Select(receipt => new { receipt.Id, receipt.UserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return MissionReceiptProcessingOutcome.NoWork;
        }

        return await ProcessAsync(candidate.Id, cancellationToken);
    }

    public async Task<MissionReceiptProcessingOutcome> ProcessAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await dbContext.ActionReceipts
            .AsNoTracking()
            .Where(receipt => receipt.Id == receiptId)
            .Select(receipt => new { receipt.Id, receipt.UserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return MissionReceiptProcessingOutcome.NoWork;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await EnsureWalletExistsAsync(candidate.UserId, cancellationToken);
        var walletLock = await walletEngine.AcquireLocksAsync(candidate.UserId, cancellationToken);
        walletLock = await walletEngine.ValidateReconciliationAsync(walletLock, cancellationToken);
        if (walletLock.Outcome != SpendCoinsOutcome.Succeeded ||
            walletLock.Wallet is null)
        {
            throw new InvalidOperationException(
                "Wallet state is not valid for a Mission reward decision.");
        }
        await criticalSectionObserver.WalletLockedAsync(
            candidate.UserId,
            cancellationToken);

        var receipt = await dbContext.ActionReceipts
            .FromSqlInterpolated($"""
                SELECT *
                FROM missions.action_receipts
                WHERE id = {candidate.Id}
                FOR UPDATE
                """)
            .SingleAsync(cancellationToken);
        if (receipt.ProcessingStatus == ActionReceiptStatuses.Processed)
        {
            await transaction.CommitAsync(cancellationToken);
            return MissionReceiptProcessingOutcome.Processed;
        }

        var actionType = MapActionType(receipt.SourceEventType);
        var definitions = await dbContext.MissionDefinitions
            .FromSqlInterpolated($"""
                SELECT *
                FROM missions.mission_definitions
                WHERE action_type = {actionType}
                ORDER BY id
                FOR SHARE
                """)
            .ToArrayAsync(cancellationToken);
        var decisionAt = PostgreSqlTimestamp.Normalize(
            await GetServerTimeAsync(cancellationToken));
        var definitionIds = definitions.Select(item => item.Id).ToArray();
        var activations = await dbContext.MissionActivations
            .AsNoTracking()
            .Where(item => definitionIds.Contains(item.MissionDefinitionId) &&
                item.ActivatedAt <= receipt.OccurredAt &&
                (item.DeactivatedAt == null || receipt.OccurredAt < item.DeactivatedAt))
            .OrderBy(item => item.MissionDefinitionId)
            .ThenBy(item => item.ActivationSequence)
            .ToArrayAsync(cancellationToken);

        foreach (var ambiguous in activations.GroupBy(item => item.MissionDefinitionId)
                     .Where(group => group.Skip(1).Any()))
        {
            throw new InvalidOperationException(
                $"Mission activation history is ambiguous for {ambiguous.Key}.");
        }

        var activationByMissionId = activations.ToDictionary(item => item.MissionDefinitionId);
        var revisionIds = activations.Select(item => item.MissionRevisionId).ToArray();
        var revisions = await dbContext.MissionRevisions
            .AsNoTracking()
            .Where(item => revisionIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        foreach (var definition in definitions.OrderBy(item => item.Id))
        {
            if (!activationByMissionId.TryGetValue(definition.Id, out var activation))
            {
                continue;
            }

            if (!revisions.TryGetValue(activation.MissionRevisionId, out var revision) ||
                revision.MissionId != definition.Id || !revision.AppliesAt(receipt.OccurredAt) ||
                !RepeatPolicyMatches(definition.ActionType, revision.RepeatPolicy))
            {
                throw new InvalidOperationException(
                    "Historical Mission activation state is inconsistent.");
            }

            var period = periodResolver.Resolve(
                definition.CycleType,
                receipt.OccurredAt,
                revision.EffectiveFrom,
                revision.EffectiveUntil);
            await ApplyActionAsync(
                receipt,
                revision,
                period,
                walletLock.Wallet,
                decisionAt,
                cancellationToken);
        }

        receipt.MarkProcessed();
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return MissionReceiptProcessingOutcome.Processed;
    }

    private async Task ApplyActionAsync(
        ActionReceipt receipt,
        MissionRevision revision,
        MissionPeriod period,
        WalletAccount wallet,
        DateTimeOffset decisionAt,
        CancellationToken cancellationToken)
    {
        var progress = await dbContext.MissionProgress.SingleOrDefaultAsync(item =>
            item.UserId == receipt.UserId && item.MissionRevisionId == revision.Id &&
            item.PeriodKey == period.Key,
            cancellationToken);
        if (progress is null)
        {
            progress = MissionProgress.Create(
                Guid.NewGuid(), receipt.UserId, revision.Id, period.Key);
            dbContext.MissionProgress.Add(progress);
        }

        if (progress.Status == MissionProgressStatuses.Completed)
        {
            return;
        }

        var dedupKey = revision.RepeatPolicy switch
        {
            MissionRepeatPolicies.OncePerVietnamDate =>
                $"date:{receipt.OccurredLocalDate:yyyy-MM-dd}",
            MissionRepeatPolicies.UniqueSubjectPerPeriod when receipt.SubjectId is { } subjectId =>
                $"subject:{subjectId:D}",
            _ => throw new InvalidOperationException(
                "Mission receipt does not satisfy its revision repeat policy."),
        };
        if (await dbContext.ProgressActions.AnyAsync(item =>
                item.MissionProgressId == progress.Id && item.DedupKey == dedupKey,
                cancellationToken))
        {
            return;
        }

        dbContext.ProgressActions.Add(ProgressAction.Create(
            progress.Id,
            receipt.SourceEventId,
            receipt.SubjectId,
            dedupKey,
            receipt.OccurredAt));
        if (!progress.Increment(revision.TargetCount, receipt.OccurredAt))
        {
            return;
        }

        await CreateRewardDecisionAsync(
            receipt,
            revision,
            progress,
            wallet,
            decisionAt,
            cancellationToken);
    }

    private async Task CreateRewardDecisionAsync(
        ActionReceipt receipt,
        MissionRevision revision,
        MissionProgress progress,
        WalletAccount wallet,
        DateTimeOffset decisionAt,
        CancellationToken cancellationToken)
    {
        if (await dbContext.MissionRewardGrants.AnyAsync(
                item => item.ProgressId == progress.Id,
                cancellationToken))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO missions.daily_counters
                (user_id, local_date, granted_total, version)
            VALUES ({receipt.UserId}, {receipt.OccurredLocalDate}, 0, 1)
            ON CONFLICT (user_id, local_date) DO NOTHING
            """, cancellationToken);
        var counter = await dbContext.MissionDailyCounters
            .FromSqlInterpolated($"""
                SELECT *
                FROM missions.daily_counters
                WHERE user_id = {receipt.UserId}
                  AND local_date = {receipt.OccurredLocalDate}
                FOR UPDATE
                """)
            .SingleAsync(cancellationToken);

        var rewardId = Guid.NewGuid();
        if (!counter.TryGrant(revision.RewardBonusCoins))
        {
            dbContext.MissionRewardGrants.Add(MissionRewardGrant.Create(
                rewardId,
                progress.Id,
                receipt.UserId,
                revision.RewardBonusCoins,
                MissionRewardOutcomes.DeniedDailyCap,
                receipt.OccurredLocalDate,
                decisionAt,
                null));
            return;
        }

        var operationId = Guid.NewGuid();
        wallet.CreditBonus(revision.RewardBonusCoins, decisionAt);
        var operation = WalletOperation.Create(
            operationId,
            receipt.UserId,
            WalletOperationTypes.MissionReward,
            WalletOperationSourceTypes.RewardGrant,
            rewardId,
            null,
            decisionAt);
        dbContext.WalletOperations.Add(operation);
        dbContext.WalletEntries.Add(WalletEntry.Create(
            Guid.NewGuid(),
            operation,
            WalletCoinTypes.Bonus,
            WalletBuckets.Available,
            revision.RewardBonusCoins,
            1,
            decisionAt));
        dbContext.MissionRewardGrants.Add(MissionRewardGrant.Create(
            rewardId,
            progress.Id,
            receipt.UserId,
            revision.RewardBonusCoins,
            MissionRewardOutcomes.Granted,
            receipt.OccurredLocalDate,
            decisionAt,
            operationId));
        outboxWriter.Enqueue(
            MissionEventTypes.RewardGranted,
            new MissionRewardGranted(
                rewardId,
                receipt.UserId,
                revision.Id,
                progress.Id,
                revision.RewardBonusCoins,
                receipt.OccurredLocalDate,
                decisionAt),
            decisionAt);
    }

    private async Task EnsureWalletExistsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wallet.wallets
                (user_id, paid_available, paid_reserved, bonus_available,
                 version, created_at, updated_at)
            VALUES
                ({userId}, 0, 0, 0, 1, clock_timestamp(), clock_timestamp())
            ON CONFLICT (user_id) DO NOTHING
            """, cancellationToken);

    private Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<DateTimeOffset>(
                "SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);

    private static bool RepeatPolicyMatches(string actionType, string repeatPolicy) =>
        actionType == MissionActionTypes.DailyLogin
            ? repeatPolicy == MissionRepeatPolicies.OncePerVietnamDate
            : repeatPolicy == MissionRepeatPolicies.UniqueSubjectPerPeriod;

    private static string MapActionType(string sourceEventType) => sourceEventType switch
    {
        IdentityEventTypes.UserLoggedIn => MissionActionTypes.DailyLogin,
        ReadingEventTypes.ChapterReadQualified => MissionActionTypes.ChapterReadQualified,
        ReadingEventTypes.ComicFollowed => MissionActionTypes.ComicFollowed,
        ReadingEventTypes.ChapterUnlocked => MissionActionTypes.ChapterUnlocked,
        BillingEventTypes.WalletTopUpCompleted => MissionActionTypes.WalletTopUpCompleted,
        _ => throw new InvalidOperationException("Unsupported Mission receipt event type."),
    };
}
