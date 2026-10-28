using ComicManagement.Administration.Reporting;
using ComicManagement.Administration.Auditing;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Missions.Rewards;
using ComicManagement.Reading.Events;
using ComicManagement.Shared.Pagination;
using ComicManagement.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ComicManagement.Persistence;

internal sealed class AdministrationReportingStore(
    AppDbContext dbContext,
    IConsistentReadExecutor consistentRead,
    IAdministrationReportingReadObserver observer) : IAdministrationReportingStore
{
    private static readonly JsonSerializerOptions EventSerializerOptions =
        new(JsonSerializerDefaults.Web);

    public Task<AdminDashboard> GetDashboardAsync(
        AdministrationDateRange range,
        CancellationToken cancellationToken = default) =>
        consistentRead.ExecuteAsync(async token =>
        {
            _ = await dbContext.PaymentOrders.AsNoTracking().AnyAsync(token);
            await observer.AfterDashboardSnapshotEstablishedAsync(token);
            var payments = await dbContext.PaymentOrders.AsNoTracking()
                .Where(order => order.OrderStatus == TopUpOrderStatuses.Succeeded &&
                    order.ProviderPaymentStatus == ProviderPaymentStatuses.Verified &&
                    order.FulfillmentStatus == PaymentFulfillmentStatuses.Fulfilled &&
                    order.VerifiedPaidAt >= range.StartsAtUtc &&
                    order.VerifiedPaidAt < range.EndsAtUtc)
                .Select(order => new DashboardPaymentObservation(
                    order.OrderType,
                    order.AmountVnd,
                    dbContext.PaidCoinLots
                        .Where(lot => lot.TopUpOrderId == order.Id)
                        .Select(lot => (long?)lot.GrantedCoins)
                        .SingleOrDefault() ?? 0,
                    order.VerifiedPaidAt!.Value))
                .ToListAsync(token);

            var refunds = await dbContext.RefundRequests.AsNoTracking()
                .Where(refund => refund.Status == RefundRequestStatuses.Completed &&
                    refund.CompletedAt >= range.StartsAtUtc &&
                    refund.CompletedAt < range.EndsAtUtc)
                .Select(refund => new DashboardRefundObservation(
                    refund.OriginalAmountVnd,
                    refund.FeeAmountVnd,
                    refund.NetRefundVnd,
                    refund.CompletedAt!.Value))
                .ToListAsync(token);

            var readingEvents = await dbContext.OutboxMessages.AsNoTracking()
                .Where(message => message.EventType == ReadingEventTypes.ChapterReadQualified &&
                    message.OccurredAt >= range.StartsAtUtc &&
                    message.OccurredAt < range.EndsAtUtc)
                .Select(message => message.Payload)
                .ToListAsync(token);
            var readings = readingEvents
                .Select(payload => JsonSerializer.Deserialize<ChapterReadQualified>(
                    payload,
                    EventSerializerOptions)
                    ?? throw new InvalidOperationException(
                        "A ChapterReadQualified outbox payload is invalid."))
                .Select(@event => new DashboardReadingObservation(@event.UserId, @event.QualifiedAt))
                .ToList();

            var receipts = await dbContext.ActionReceipts.AsNoTracking()
                .Where(receipt => receipt.OccurredAt >= range.StartsAtUtc &&
                    receipt.OccurredAt < range.EndsAtUtc)
                .Select(receipt => receipt.OccurredAt)
                .ToListAsync(token);

            var rewards = await dbContext.MissionRewardGrants.AsNoTracking()
                .Where(reward => reward.DecidedAt >= range.StartsAtUtc &&
                    reward.DecidedAt < range.EndsAtUtc)
                .Select(reward => new DashboardMissionRewardObservation(
                    reward.Outcome,
                    reward.BonusCoins,
                    reward.DecidedAt))
                .ToListAsync(token);

            return AdministrationDashboardCalculator.Calculate(
                range, payments, refunds, readings, receipts, rewards);
        }, cancellationToken);

    public Task<AdminAuditPage> ListAuditAsync(
        AdminAuditCriteria criteria,
        CancellationToken cancellationToken = default) =>
        consistentRead.ExecuteAsync(async token =>
        {
            if (!OffsetPage.TryCreate(
                    criteria.Page,
                    criteria.PageSize,
                    AdministrationReportingHandler.MaximumPageSize,
                    out var page))
            {
                throw new ArgumentOutOfRangeException(nameof(criteria));
            }

            _ = await dbContext.AuditLogs.AsNoTracking().AnyAsync(token);
            await observer.AfterAuditSnapshotEstablishedAsync(token);
            var query = dbContext.AuditLogs.AsNoTracking()
                .Where(audit => audit.OccurredAt >= criteria.Range.StartsAtUtc &&
                    audit.OccurredAt < criteria.Range.EndsAtUtc);
            if (criteria.ActorId is { } actorId)
                query = query.Where(audit => audit.ActorId == actorId);
            if (criteria.Action is { } action)
                query = query.Where(audit => audit.Action == action);
            if (criteria.TargetType is { } targetType)
                query = query.Where(audit => audit.TargetType == targetType);
            if (criteria.TargetId is { } targetId)
                query = query.Where(audit => audit.TargetId == targetId);

            var totalItems = await query.LongCountAsync(token);
            var items = await query
                .OrderByDescending(audit => audit.OccurredAt)
                .ThenByDescending(audit => audit.Id)
                .Skip(page.Offset)
                .Take(page.PageSize)
                .Select(audit => new AdminAuditItem(
                    audit.Id,
                    audit.ActorId,
                    audit.Action,
                    audit.TargetType,
                    audit.TargetId,
                    audit.OccurredAt,
                    AuditRedactionPolicy.SanitizeForExposure(audit.RedactedChanges)))
                .ToListAsync(token);
            return new AdminAuditPage(
                items,
                page.Page,
                page.PageSize,
                totalItems,
                page.TotalPages(totalItems));
        }, cancellationToken);

}
