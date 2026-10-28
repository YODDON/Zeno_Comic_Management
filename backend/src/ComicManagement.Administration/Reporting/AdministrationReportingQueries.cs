using ComicManagement.Shared.Pagination;

namespace ComicManagement.Administration.Reporting;

public sealed record AdministrationDateRange(
    DateOnly From,
    DateOnly To,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc);

public sealed record RevenueMetrics(long TopUpGrossVnd, long VipGrossVnd);

public sealed record RefundMetrics(
    long CompletedCount,
    long OriginalAmountVnd,
    long FeeVnd,
    long NetRefundVnd);

public sealed record CoinIssuanceMetrics(long PaidCoins, long BonusCoins);

public sealed record ReadingMetrics(long QualifiedSessionCount, long DistinctReaderCount);

public sealed record MissionMetrics(
    long ActionReceiptCount,
    long GrantedRewardCount,
    long DeniedDailyCapCount,
    long GrantedBonusCoins);

public sealed record DashboardDailyPoint(
    DateOnly Date,
    long TopUpGrossVnd,
    long VipGrossVnd,
    long CompletedRefundCount,
    long RefundOriginalAmountVnd,
    long RefundFeeVnd,
    long RefundNetVnd,
    long PaidCoinsIssued,
    long BonusCoinsGranted,
    long QualifiedSessionCount,
    long DistinctReaderCount,
    long ActionReceiptCount,
    long GrantedRewardCount,
    long DeniedDailyCapCount);

public sealed record AdminDashboard(
    AdministrationDateRange Range,
    RevenueMetrics Revenue,
    RefundMetrics Refunds,
    CoinIssuanceMetrics CoinsIssued,
    ReadingMetrics Reading,
    MissionMetrics Missions,
    IReadOnlyList<DashboardDailyPoint> DailySeries);

public sealed record DashboardPaymentObservation(
    string OrderType,
    long AmountVnd,
    long PaidCoinsIssued,
    DateTimeOffset OccurredAt);

public sealed record DashboardRefundObservation(
    long OriginalAmountVnd,
    long FeeVnd,
    long NetRefundVnd,
    DateTimeOffset OccurredAt);

public sealed record DashboardReadingObservation(Guid UserId, DateTimeOffset OccurredAt);

public sealed record DashboardMissionRewardObservation(
    string Outcome,
    long BonusCoins,
    DateTimeOffset OccurredAt);

public static class AdministrationDashboardCalculator
{
    public static AdminDashboard Calculate(
        AdministrationDateRange range,
        IReadOnlyList<DashboardPaymentObservation> payments,
        IReadOnlyList<DashboardRefundObservation> refunds,
        IReadOnlyList<DashboardReadingObservation> readings,
        IReadOnlyList<DateTimeOffset> receiptTimes,
        IReadOnlyList<DashboardMissionRewardObservation> rewards)
    {
        var topUps = payments.Where(item => item.OrderType == "TopUp").ToList();
        var vipPurchases = payments.Where(item => item.OrderType == "VipPurchase").ToList();
        var grantedRewards = rewards.Where(item => item.Outcome == "Granted").ToList();
        var deniedRewards = rewards.Where(item => item.Outcome == "DeniedDailyCap").ToList();
        var daily = Enumerable.Range(0, range.To.DayNumber - range.From.DayNumber + 1)
            .Select(offset => BuildDailyPoint(
                range.From.AddDays(offset), topUps, vipPurchases, refunds,
                readings, receiptTimes, grantedRewards, deniedRewards))
            .ToList();

        return new AdminDashboard(
            range,
            new RevenueMetrics(
                topUps.Sum(item => item.AmountVnd),
                vipPurchases.Sum(item => item.AmountVnd)),
            new RefundMetrics(
                refunds.Count,
                refunds.Sum(item => item.OriginalAmountVnd),
                refunds.Sum(item => item.FeeVnd),
                refunds.Sum(item => item.NetRefundVnd)),
            new CoinIssuanceMetrics(
                topUps.Sum(item => item.PaidCoinsIssued),
                grantedRewards.Sum(item => item.BonusCoins)),
            new ReadingMetrics(readings.Count, readings.Select(item => item.UserId).Distinct().LongCount()),
            new MissionMetrics(
                receiptTimes.Count,
                grantedRewards.Count,
                deniedRewards.Count,
                grantedRewards.Sum(item => item.BonusCoins)),
            daily);
    }

    private static DashboardDailyPoint BuildDailyPoint(
        DateOnly date,
        IReadOnlyList<DashboardPaymentObservation> topUps,
        IReadOnlyList<DashboardPaymentObservation> vipPurchases,
        IReadOnlyList<DashboardRefundObservation> refunds,
        IReadOnlyList<DashboardReadingObservation> readings,
        IReadOnlyList<DateTimeOffset> receipts,
        IReadOnlyList<DashboardMissionRewardObservation> grantedRewards,
        IReadOnlyList<DashboardMissionRewardObservation> deniedRewards)
    {
        var topUpDay = topUps.Where(item => VietnamDate(item.OccurredAt) == date).ToList();
        var vipDay = vipPurchases.Where(item => VietnamDate(item.OccurredAt) == date).ToList();
        var refundDay = refunds.Where(item => VietnamDate(item.OccurredAt) == date).ToList();
        var readingDay = readings.Where(item => VietnamDate(item.OccurredAt) == date).ToList();
        var grantedDay = grantedRewards.Where(item => VietnamDate(item.OccurredAt) == date).ToList();
        var deniedDay = deniedRewards.Where(item => VietnamDate(item.OccurredAt) == date).ToList();

        return new DashboardDailyPoint(
            date,
            topUpDay.Sum(item => item.AmountVnd),
            vipDay.Sum(item => item.AmountVnd),
            refundDay.Count,
            refundDay.Sum(item => item.OriginalAmountVnd),
            refundDay.Sum(item => item.FeeVnd),
            refundDay.Sum(item => item.NetRefundVnd),
            topUpDay.Sum(item => item.PaidCoinsIssued),
            grantedDay.Sum(item => item.BonusCoins),
            readingDay.Count,
            readingDay.Select(item => item.UserId).Distinct().LongCount(),
            receipts.LongCount(timestamp => VietnamDate(timestamp) == date),
            grantedDay.Count,
            deniedDay.Count);
    }

    private static DateOnly VietnamDate(DateTimeOffset timestamp) =>
        AdministrationReportingHandler.GetVietnamDate(timestamp);
}

public sealed record AdminAuditItem(
    Guid Id,
    Guid ActorId,
    string Action,
    string TargetType,
    Guid TargetId,
    DateTimeOffset OccurredAt,
    string RedactedChanges);

public sealed record AdminAuditPage(
    IReadOnlyList<AdminAuditItem> Items,
    int Page,
    int PageSize,
    long TotalItems,
    int TotalPages);

public sealed record AdminAuditCriteria(
    AdministrationDateRange Range,
    Guid? ActorId,
    string? Action,
    string? TargetType,
    Guid? TargetId,
    int Page,
    int PageSize);

public interface IAdministrationReportingStore
{
    Task<AdminDashboard> GetDashboardAsync(
        AdministrationDateRange range,
        CancellationToken cancellationToken = default);

    Task<AdminAuditPage> ListAuditAsync(
        AdminAuditCriteria criteria,
        CancellationToken cancellationToken = default);
}

public interface IAdministrationReportingReadObserver
{
    Task AfterDashboardSnapshotEstablishedAsync(CancellationToken cancellationToken);

    Task AfterAuditSnapshotEstablishedAsync(CancellationToken cancellationToken);
}

public sealed class NoOpAdministrationReportingReadObserver : IAdministrationReportingReadObserver
{
    public Task AfterDashboardSnapshotEstablishedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task AfterAuditSnapshotEstablishedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public enum AdministrationReportingOutcome
{
    Succeeded,
    ValidationFailed,
}

public sealed record AdministrationReportingResult<T>(
    AdministrationReportingOutcome Outcome,
    T? Value = default);

public sealed class AdministrationReportingHandler(IAdministrationReportingStore store)
{
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 100;
    public const int MaximumRangeDays = 366;
    public const int MaximumFilterLength = 120;
    public const string VietnamTimeZoneId = "Asia/Ho_Chi_Minh";

    public Task<AdministrationReportingResult<AdminDashboard>> GetDashboardAsync(
        DateOnly? from,
        DateOnly? to,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(from, to, today);
        return range is null
            ? Task.FromResult(new AdministrationReportingResult<AdminDashboard>(
                AdministrationReportingOutcome.ValidationFailed))
            : ExecuteDashboardAsync(range, cancellationToken);
    }

    public Task<AdministrationReportingResult<AdminAuditPage>> ListAuditAsync(
        DateOnly? from,
        DateOnly? to,
        DateOnly today,
        Guid? actorId,
        string? action,
        string? targetType,
        Guid? targetId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(from, to, today);
        var normalizedAction = Normalize(action);
        var normalizedTargetType = Normalize(targetType);
        if (range is null ||
            !OffsetPage.TryCreate(page, pageSize, MaximumPageSize, out _) ||
            actorId == Guid.Empty || targetId == Guid.Empty ||
            normalizedAction?.Length > MaximumFilterLength ||
            normalizedTargetType?.Length > MaximumFilterLength)
        {
            return Task.FromResult(new AdministrationReportingResult<AdminAuditPage>(
                AdministrationReportingOutcome.ValidationFailed));
        }

        return ExecuteAuditAsync(new AdminAuditCriteria(
            range,
            actorId,
            normalizedAction,
            normalizedTargetType,
            targetId,
            page,
            pageSize), cancellationToken);
    }

    public static AdministrationDateRange? ResolveRange(
        DateOnly? from,
        DateOnly? to,
        DateOnly today)
    {
        var resolvedTo = to ?? today;
        var resolvedFrom = from ?? resolvedTo.AddDays(-29);
        var days = resolvedTo.DayNumber - resolvedFrom.DayNumber + 1;
        if (days is < 1 or > MaximumRangeDays)
        {
            return null;
        }

        var timeZone = FindVietnamTimeZone();
        return new AdministrationDateRange(
            resolvedFrom,
            resolvedTo,
            ToUtc(resolvedFrom, timeZone),
            ToUtc(resolvedTo.AddDays(1), timeZone));
    }

    public static DateOnly GetVietnamToday(DateTimeOffset utcNow)
    {
        if (utcNow.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC.", nameof(utcNow));
        }

        return DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(utcNow, FindVietnamTimeZone()).DateTime);
    }

    public static DateOnly GetVietnamDate(DateTimeOffset utcTimestamp) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(utcTimestamp, FindVietnamTimeZone()).DateTime);

    private async Task<AdministrationReportingResult<AdminDashboard>> ExecuteDashboardAsync(
        AdministrationDateRange range,
        CancellationToken cancellationToken) => new(
        AdministrationReportingOutcome.Succeeded,
        await store.GetDashboardAsync(range, cancellationToken));

    private async Task<AdministrationReportingResult<AdminAuditPage>> ExecuteAuditAsync(
        AdminAuditCriteria criteria,
        CancellationToken cancellationToken) => new(
        AdministrationReportingOutcome.Succeeded,
        await store.ListAuditAsync(criteria, cancellationToken));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset ToUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    private static TimeZoneInfo FindVietnamTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(VietnamTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        }
    }
}
