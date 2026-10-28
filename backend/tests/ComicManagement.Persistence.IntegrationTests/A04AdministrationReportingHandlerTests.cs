using ComicManagement.Administration.Reporting;
using ComicManagement.Administration.Auditing;
using System.Text.Json;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class A04AdministrationReportingHandlerTests
{
    private readonly CapturingStore _store = new();

    [Fact]
    public async Task Default_range_is_thirty_inclusive_Vietnam_days()
    {
        var handler = new AdministrationReportingHandler(_store);
        var result = await handler.GetDashboardAsync(null, null, new DateOnly(2026, 10, 28));

        Assert.Equal(AdministrationReportingOutcome.Succeeded, result.Outcome);
        Assert.Equal(new DateOnly(2026, 9, 29), _store.Range!.From);
        Assert.Equal(new DateOnly(2026, 10, 28), _store.Range.To);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 17, 0, 0, TimeSpan.Zero), _store.Range.StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 17, 0, 0, TimeSpan.Zero), _store.Range.EndsAtUtc);
    }

    [Fact]
    public async Task Range_allows_366_days_and_rejects_invalid_or_367_days()
    {
        var handler = new AdministrationReportingHandler(_store);
        var valid = await handler.GetDashboardAsync(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), new DateOnly(2026, 1, 1));
        var tooLong = await handler.GetDashboardAsync(
            new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var reversed = await handler.GetDashboardAsync(
            new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1));

        Assert.Equal(AdministrationReportingOutcome.Succeeded, valid.Outcome);
        Assert.Equal(AdministrationReportingOutcome.ValidationFailed, tooLong.Outcome);
        Assert.Equal(AdministrationReportingOutcome.ValidationFailed, reversed.Outcome);
    }

    [Fact]
    public async Task Audit_rejects_offset_overflow_and_oversized_filters()
    {
        var handler = new AdministrationReportingHandler(_store);
        var overflow = await handler.ListAuditAsync(
            null, null, new DateOnly(2026, 10, 28), null, null, null, null,
            int.MaxValue, 100);
        var oversized = await handler.ListAuditAsync(
            null, null, new DateOnly(2026, 10, 28), null,
            new string('a', AdministrationReportingHandler.MaximumFilterLength + 1),
            null, null, 1, 30);

        Assert.Equal(AdministrationReportingOutcome.ValidationFailed, overflow.Outcome);
        Assert.Equal(AdministrationReportingOutcome.ValidationFailed, oversized.Outcome);
    }

    [Fact]
    public void Dashboard_calculator_keeps_semantics_separate_and_buckets_by_Vietnam_date()
    {
        var range = AdministrationReportingHandler.ResolveRange(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 2),
            new DateOnly(2026, 10, 2))!;
        var reader = Guid.NewGuid();
        var dashboard = AdministrationDashboardCalculator.Calculate(
            range,
            [
                new("TopUp", 100_000, 10_000, new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero)),
                new("VipPurchase", 29_000, 0, new DateTimeOffset(2026, 10, 1, 16, 59, 59, TimeSpan.Zero)),
            ],
            [new(100_000, 5_000, 95_000, new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero))],
            [
                new(reader, new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero)),
                new(reader, new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero)),
            ],
            [new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero)],
            [
                new("Granted", 20, new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero)),
                new("DeniedDailyCap", 30, new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero)),
            ]);

        Assert.Equal(100_000, dashboard.Revenue.TopUpGrossVnd);
        Assert.Equal(29_000, dashboard.Revenue.VipGrossVnd);
        Assert.Equal(new RefundMetrics(1, 100_000, 5_000, 95_000), dashboard.Refunds);
        Assert.Equal(new CoinIssuanceMetrics(10_000, 20), dashboard.CoinsIssued);
        Assert.Equal(new ReadingMetrics(2, 1), dashboard.Reading);
        Assert.Equal(new MissionMetrics(1, 1, 1, 20), dashboard.Missions);
        Assert.Equal(129_000, dashboard.DailySeries[0].TopUpGrossVnd + dashboard.DailySeries[0].VipGrossVnd);
        Assert.Equal(0, dashboard.DailySeries[0].CompletedRefundCount);
        Assert.Equal(1, dashboard.DailySeries[1].CompletedRefundCount);
        Assert.Equal(2, dashboard.DailySeries[1].QualifiedSessionCount);
    }

    [Fact]
    public void Audit_redaction_is_recursive_canonical_and_fails_closed_for_malformed_legacy_json()
    {
        const string input =
            "{\"z\":1,\"accountNumber\":\"0123456789\",\"nested\":{" +
            "\"safe\":true,\"api_key\":\"secret\"},\"items\":[{" +
            "\"providerPayload\":\"raw\",\"code\":\"OK\"}]}";

        var first = AuditRedactionPolicy.CanonicalizeForPersistence(input);
        var second = AuditRedactionPolicy.CanonicalizeForPersistence(input);
        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        Assert.True(document.RootElement.GetProperty("nested").GetProperty("safe").GetBoolean());
        Assert.Equal("OK", document.RootElement.GetProperty("items")[0]
            .GetProperty("code").GetString());
        Assert.DoesNotContain("0123456789", first, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", first, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", first, StringComparison.Ordinal);
        Assert.Equal("{}", AuditRedactionPolicy.SanitizeForExposure("not-json-sensitive-data"));
    }

    private sealed class CapturingStore : IAdministrationReportingStore
    {
        public AdministrationDateRange? Range { get; private set; }

        public Task<AdminDashboard> GetDashboardAsync(
            AdministrationDateRange range,
            CancellationToken cancellationToken = default)
        {
            Range = range;
            return Task.FromResult(new AdminDashboard(
                range,
                new(0, 0),
                new(0, 0, 0, 0),
                new(0, 0),
                new(0, 0),
                new(0, 0, 0, 0),
                []));
        }

        public Task<AdminAuditPage> ListAuditAsync(
            AdminAuditCriteria criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminAuditPage([], criteria.Page, criteria.PageSize, 0, 0));
    }
}
