using ComicManagement.Administration.Auditing;
using ComicManagement.Administration.Reporting;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Identity.Users;
using ComicManagement.Membership.Plans;
using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Progress;
using ComicManagement.Missions.Receipts;
using ComicManagement.Missions.Rewards;
using ComicManagement.Reading.Events;
using ComicManagement.Shared.Messaging;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class A04AdministrationReportingPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 28, 2, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("a04_reporting").WithUsername("postgres").WithPassword("postgres").Build();
    private readonly ReportingGateObserver _observer = new();
    private ServiceProvider _services = null!;
    private readonly Guid _adminId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var services = new ServiceCollection().AddPersistence(_postgres.GetConnectionString());
        services.AddSingleton<IRefundRecipientProtector, A04RefundRecipientProtector>();
        services.RemoveAll<IAdministrationReportingReadObserver>();
        services.AddSingleton<IAdministrationReportingReadObserver>(_observer);
        _services = services.BuildServiceProvider(true);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        db.Set<User>().Add(User.CreateFromGoogle(
            _adminId, "A04 Admin", "a04@example.test", null, BaseTime.AddDays(-1)));
        AddFulfilledTopUp(db, Guid.NewGuid(), "A04-INITIAL", 100_000, BaseTime);
        db.Set<AuditLog>().Add(AuditLog.Create(
            Guid.NewGuid(), _adminId, "A04.Initial", "PaymentOrder", Guid.NewGuid(),
            "a04-initial", BaseTime, "{\"status\":\"Succeeded\"}"));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Dashboard_uses_one_repeatable_read_snapshot_for_all_metrics_and_series()
    {
        _observer.ArmDashboard();
        await using var queryScope = _services.CreateAsyncScope();
        var query = queryScope.ServiceProvider.GetRequiredService<AdministrationReportingHandler>()
            .GetDashboardAsync(
                new DateOnly(2026, 10, 28),
                new DateOnly(2026, 10, 28),
                new DateOnly(2026, 10, 28));
        await _observer.DashboardReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var writerScope = _services.CreateAsyncScope())
        {
            var writer = writerScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AddFulfilledTopUp(writer, Guid.NewGuid(), "A04-LATE", 200_000, BaseTime.AddHours(1));
            await writer.SaveChangesAsync();
        }
        _observer.ReleaseDashboard();

        var result = await query;
        Assert.Equal(AdministrationReportingOutcome.Succeeded, result.Outcome);
        Assert.Equal(100_000, result.Value!.Revenue.TopUpGrossVnd);
        Assert.Equal(10_000, result.Value.CoinsIssued.PaidCoins);
        Assert.Equal(100_000, Assert.Single(result.Value.DailySeries).TopUpGrossVnd);
    }

    [Fact]
    public async Task Audit_count_and_items_share_snapshot_and_order_by_time_then_id()
    {
        _observer.ArmAudit();
        await using var queryScope = _services.CreateAsyncScope();
        var query = queryScope.ServiceProvider.GetRequiredService<AdministrationReportingHandler>()
            .ListAuditAsync(
                new DateOnly(2026, 10, 28),
                new DateOnly(2026, 10, 28),
                new DateOnly(2026, 10, 28),
                _adminId, null, null, null, 1, 30);
        await _observer.AuditReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var writerScope = _services.CreateAsyncScope())
        {
            var writer = writerScope.ServiceProvider.GetRequiredService<AppDbContext>();
            writer.Set<AuditLog>().Add(AuditLog.Create(
                Guid.NewGuid(), _adminId, "A04.Late", "PaymentOrder", Guid.NewGuid(),
                "a04-late", BaseTime.AddMinutes(1), "{\"late\":true}"));
            await writer.SaveChangesAsync();
        }
        _observer.ReleaseAudit();

        var result = await query;
        Assert.Equal(1, result.Value!.TotalItems);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("A04.Initial", item.Action);
        Assert.DoesNotContain("account", item.RedactedChanges, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_equal_timestamps_use_descending_id_as_deterministic_tie_breaker()
    {
        var lowerId = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var higherId = Guid.Parse("00000000-0000-4000-8000-000000000002");
        var targetId = Guid.Parse("00000000-0000-4000-8000-000000000099");
        await using (var writerScope = _services.CreateAsyncScope())
        {
            var writer = writerScope.ServiceProvider.GetRequiredService<AppDbContext>();
            writer.Set<AuditLog>().AddRange(
                AuditLog.Create(
                    lowerId, _adminId, "A04.Tied", "PaymentOrder", targetId,
                    "a04-tied-lower", BaseTime, "{\"order\":1}"),
                AuditLog.Create(
                    higherId, _adminId, "A04.Tied", "PaymentOrder", targetId,
                    "a04-tied-higher", BaseTime, "{\"order\":2}"));
            await writer.SaveChangesAsync();
        }

        await using var queryScope = _services.CreateAsyncScope();
        var handler = queryScope.ServiceProvider.GetRequiredService<AdministrationReportingHandler>();
        var firstPage = await handler.ListAuditAsync(
                new DateOnly(2026, 10, 28),
                new DateOnly(2026, 10, 28),
                new DateOnly(2026, 10, 28),
                _adminId, "A04.Tied", "PaymentOrder", targetId, 1, 1);
        var secondPage = await handler.ListAuditAsync(
            new DateOnly(2026, 10, 28),
            new DateOnly(2026, 10, 28),
            new DateOnly(2026, 10, 28),
            _adminId, "A04.Tied", "PaymentOrder", targetId, 2, 1);

        Assert.Equal(2, firstPage.Value!.TotalItems);
        Assert.Equal(2, firstPage.Value.TotalPages);
        Assert.Equal(higherId, Assert.Single(firstPage.Value.Items).Id);
        Assert.Equal(lowerId, Assert.Single(secondPage.Value!.Items).Id);
    }

    [Fact]
    public async Task Audit_write_and_read_boundaries_remove_nested_sensitive_values_but_keep_safe_fields()
    {
        const string adversarial =
            "{\"status\":\"Succeeded\",\"account_number\":\"0123456789\"," +
            "\"CIPHERTEXT\":\"protected-cipher\",\"Password\":\"password-value\"," +
            "\"nested\":{\"apiKey\":\"secret-key\"," +
            "\"refresh_TOKEN\":\"refresh-token-value\",\"safe\":true}," +
            "\"items\":[{\"recipient\":{\"accountHolder\":\"NGUYEN VAN A\"}," +
            "\"rawProviderPayload\":\"raw-provider-data\"," +
            "\"financialPayload\":\"raw-financial-data\",\"code\":\"OK\"}]}";
        var protectedLog = AuditLog.Create(
            Guid.NewGuid(), _adminId, "A04.Protected", "PaymentOrder", Guid.NewGuid(),
            "a04-protected", BaseTime, adversarial);
        AssertSafeAuditDocument(protectedLog.RedactedChanges);

        await using (var writerScope = _services.CreateAsyncScope())
        {
            var writer = writerScope.ServiceProvider.GetRequiredService<AppDbContext>();
            writer.Set<AuditLog>().Add(protectedLog);
            var legacyId = Guid.NewGuid();
            await writer.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO administration.audit_logs
                    (id, actor_id, action, target_type, target_id,
                     correlation_id, occurred_at, redacted_changes)
                VALUES ({legacyId}, {_adminId}, {"A04.LegacySensitive"}, {"PaymentOrder"},
                        {Guid.NewGuid()}, {"a04-legacy-sensitive"}, {BaseTime},
                        CAST({adversarial} AS jsonb));
                """);
            await writer.SaveChangesAsync();
        }

        await using var queryScope = _services.CreateAsyncScope();
        var result = await queryScope.ServiceProvider
            .GetRequiredService<AdministrationReportingHandler>()
            .ListAuditAsync(
                new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 28),
                new DateOnly(2026, 10, 28), _adminId, "A04.LegacySensitive",
                "PaymentOrder", null, 1, 30);
        AssertSafeAuditDocument(Assert.Single(result.Value!.Items).RedactedChanges);
    }

    [Fact]
    public async Task Dashboard_uses_authoritative_financial_and_event_sources_at_Vietnam_boundaries()
    {
        var now = DateTimeOffset.UtcNow;
        var today = AdministrationReportingHandler.GetVietnamToday(now);
        var range = AdministrationReportingHandler.ResolveRange(today, today, today)!;
        var lower = range.StartsAtUtc;
        var justBeforeUpper = range.EndsAtUtc.AddTicks(-10);
        var exactUpper = range.EndsAtUtc;

        await using var baselineScope = _services.CreateAsyncScope();
        var baseline = (await baselineScope.ServiceProvider
            .GetRequiredService<AdministrationReportingHandler>()
            .GetDashboardAsync(today, today, today)).Value!;

        var secondReader = Guid.NewGuid();
        var includedTopUpId = Guid.NewGuid();
        var refundTopUpId = Guid.NewGuid();
        var outsideRangeTopUpId = Guid.NewGuid();
        var pendingOrderId = Guid.NewGuid();
        var completedRefundId = Guid.NewGuid();
        await using (var writerScope = _services.CreateAsyncScope())
        {
            var db = writerScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Set<User>().Add(User.CreateFromGoogle(
                secondReader, "A04 Reader Two", null, null, lower.AddDays(-2)));
            if (!await db.Set<VipPlan>().AnyAsync(plan => plan.Id == VipPlan.InitialPlanId))
                db.Set<VipPlan>().Add(VipPlan.CreateInitial(lower.AddDays(-2)));

            AddFulfilledTopUp(db, includedTopUpId, $"A04-IN-{Guid.NewGuid():N}",
                100_000, lower);
            AddFulfilledTopUp(db, refundTopUpId, $"A04-REF-{Guid.NewGuid():N}",
                100_000, lower.AddMinutes(1));
            AddFulfilledTopUp(db, outsideRangeTopUpId, $"A04-OUTSIDE-{Guid.NewGuid():N}",
                100_000, lower.AddDays(-1));
            AddFulfilledVip(db, Guid.NewGuid(), $"A04-VIP-{Guid.NewGuid():N}",
                29_000, justBeforeUpper);
            AddFulfilledVip(db, Guid.NewGuid(), $"A04-VIP-OUT-{Guid.NewGuid():N}",
                29_000, exactUpper);
            AddFulfilledTopUp(db, Guid.NewGuid(), $"A04-OUT-{Guid.NewGuid():N}",
                100_000, exactUpper, secondReader);

            var pending = PaymentOrder.CreateTopUp(
                pendingOrderId, _adminId, $"A04-PENDING-{Guid.NewGuid():N}",
                100_000, 10_000, $"a04-pending-{Guid.NewGuid():N}",
                new string('c', 64), lower, lower.AddMinutes(30));
            db.Set<PaymentOrder>().Add(pending);
            db.Set<PaymentOrder>().Add(CreateVipOrder(
                Guid.NewGuid(), $"A04-VIP-PENDING-{Guid.NewGuid():N}", lower, lower.AddMinutes(30)));
            var expiredTopUp = PaymentOrder.CreateTopUp(
                Guid.NewGuid(), _adminId, $"A04-EXPIRED-{Guid.NewGuid():N}",
                100_000, 10_000, $"a04-expired-{Guid.NewGuid():N}",
                new string('4', 64), lower, lower.AddMinutes(1));
            Assert.True(expiredTopUp.ExpireIfDue(lower.AddMinutes(1)));
            db.Set<PaymentOrder>().Add(expiredTopUp);
            var expiredVip = CreateVipOrder(
                Guid.NewGuid(), $"A04-VIP-EXPIRED-{Guid.NewGuid():N}",
                lower, lower.AddMinutes(1));
            Assert.True(expiredVip.ExpireIfDue(lower.AddMinutes(1)));
            db.Set<PaymentOrder>().Add(expiredVip);
            var providerOnly = ProviderTransaction.CreateSePay(
                Guid.NewGuid(),
                new SePayNormalizedTransaction(
                    $"a04-provider-{Guid.NewGuid():N}", new string('d', 64), 100_000,
                    ProviderTransactionDirections.Incoming, pending.OrderCode, "1234",
                    "provider-occurred-only", lower.AddMinutes(2), "ManualReview"),
                lower.AddMinutes(3));
            providerOnly.RequireReconciliation("ManualReview", pendingOrderId);
            db.Set<ProviderTransaction>().Add(providerOnly);

            var initialOrderId = await db.Set<PaymentOrder>()
                .Where(order => order.OrderCode == "A04-INITIAL")
                .Select(order => order.Id)
                .SingleAsync();
            var wallet = WalletAccount.Create(_adminId, lower.AddDays(-1));
            wallet.CreditPaid(10_000, lower);
            wallet.CreditPaid(10_000, lower.AddTicks(10));
            wallet.CreditPaid(10_000, lower.AddTicks(20));
            wallet.CreditPaid(10_000, lower.AddTicks(30));
            wallet.CreditBonus(25, lower.AddMinutes(2));
            db.Set<WalletAccount>().Add(wallet);
            AddTopUpLedger(db, initialOrderId, lower);
            AddTopUpLedger(db, includedTopUpId, lower.AddTicks(10));
            AddTopUpLedger(db, refundTopUpId, lower.AddTicks(20));
            AddTopUpLedger(db, outsideRangeTopUpId, lower.AddTicks(30));

            AddMissionObservations(db, lower, exactUpper);
            db.Set<ActionReceipt>().AddRange(
                CreateReceipt(_adminId, lower, 'a'),
                CreateReceipt(secondReader, justBeforeUpper, 'b'),
                CreateReceipt(secondReader, exactUpper, 'c'));

            var outbox = writerScope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            EnqueueReading(outbox, _adminId, Guid.NewGuid(), lower);
            EnqueueReading(outbox, _adminId, Guid.NewGuid(), justBeforeUpper);
            EnqueueReading(outbox, secondReader, Guid.NewGuid(), lower.AddMinutes(4));
            EnqueueReading(outbox, secondReader, Guid.NewGuid(), exactUpper);

            var refund = RefundRequest.Create(
                completedRefundId, refundTopUpId, _adminId, "A04 completed refund",
                100_000, 10_000, $"a04-refund-{Guid.NewGuid():N}",
                new string('e', 64), "v1", lower.AddMinutes(5));
            db.Set<RefundRequest>().Add(refund);
            db.Set<RefundRecipient>().Add(RefundRecipient.Create(
                Guid.NewGuid(), refund.Id, "VCB", [1, 2, 3], [4, 5, 6],
                "6789", "******6789", "N*** V*** A***", "v1", lower.AddMinutes(5)));
            await db.SaveChangesAsync();
        }
        await CompleteRefundAsync(completedRefundId);

        await using var queryScope = _services.CreateAsyncScope();
        var actual = (await queryScope.ServiceProvider
            .GetRequiredService<AdministrationReportingHandler>()
            .GetDashboardAsync(today, today, today)).Value!;

        Assert.Equal(baseline.Revenue.TopUpGrossVnd + 200_000,
            actual.Revenue.TopUpGrossVnd);
        Assert.Equal(baseline.Revenue.VipGrossVnd + 29_000,
            actual.Revenue.VipGrossVnd);
        Assert.Equal(baseline.CoinsIssued.PaidCoins + 20_000,
            actual.CoinsIssued.PaidCoins);
        var currentWalletPaid = await queryScope.ServiceProvider
            .GetRequiredService<AppDbContext>().Set<WalletAccount>().AsNoTracking()
            .Where(wallet => wallet.UserId == _adminId)
            .Select(wallet => wallet.PaidAvailable + wallet.PaidReserved)
            .SingleAsync();
        Assert.NotEqual(currentWalletPaid, actual.CoinsIssued.PaidCoins);
        Assert.Equal(baseline.Refunds.CompletedCount + 1, actual.Refunds.CompletedCount);
        Assert.Equal(baseline.Refunds.OriginalAmountVnd + 100_000,
            actual.Refunds.OriginalAmountVnd);
        Assert.Equal(baseline.Refunds.FeeVnd + 5_000, actual.Refunds.FeeVnd);
        Assert.Equal(baseline.Refunds.NetRefundVnd + 95_000, actual.Refunds.NetRefundVnd);
        Assert.Equal(baseline.Reading.QualifiedSessionCount + 3,
            actual.Reading.QualifiedSessionCount);
        Assert.Equal(baseline.Reading.DistinctReaderCount + 2,
            actual.Reading.DistinctReaderCount);
        Assert.Equal(baseline.Missions.ActionReceiptCount + 2,
            actual.Missions.ActionReceiptCount);
        Assert.Equal(baseline.Missions.GrantedRewardCount + 1,
            actual.Missions.GrantedRewardCount);
        Assert.Equal(baseline.Missions.DeniedDailyCapCount + 1,
            actual.Missions.DeniedDailyCapCount);
        Assert.Equal(baseline.Missions.GrantedBonusCoins + 25,
            actual.Missions.GrantedBonusCoins);
        Assert.Equal(baseline.CoinsIssued.BonusCoins + 25,
            actual.CoinsIssued.BonusCoins);

        var baselineDay = Assert.Single(baseline.DailySeries);
        var actualDay = Assert.Single(actual.DailySeries);
        Assert.Equal(today, actualDay.Date);
        Assert.Equal(baselineDay.TopUpGrossVnd + 200_000, actualDay.TopUpGrossVnd);
        Assert.Equal(baselineDay.VipGrossVnd + 29_000, actualDay.VipGrossVnd);
        Assert.Equal(baselineDay.QualifiedSessionCount + 3, actualDay.QualifiedSessionCount);
    }

    private static void AssertSafeAuditDocument(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Succeeded", document.RootElement.GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("nested").GetProperty("safe").GetBoolean());
        Assert.Equal("OK", document.RootElement.GetProperty("items")[0]
            .GetProperty("code").GetString());
        foreach (var forbidden in new[]
                 {
                     "0123456789", "NGUYEN VAN A", "secret-key", "raw-provider-data",
                     "protected-cipher", "password-value", "refresh-token-value",
                     "raw-financial-data", "account_number", "recipient", "apiKey",
                     "rawProviderPayload", "CIPHERTEXT", "Password", "refresh_TOKEN",
                     "financialPayload",
                 })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    private void AddFulfilledVip(
        AppDbContext db,
        Guid orderId,
        string orderCode,
        long amountVnd,
        DateTimeOffset paidAt)
    {
        var order = CreateVipOrder(
            orderId, orderCode, paidAt.AddMinutes(-1), paidAt.AddMinutes(29), amountVnd);
        order.FulfillVipPurchase(paidAt);
        db.Set<PaymentOrder>().Add(order);
    }

    private PaymentOrder CreateVipOrder(
        Guid orderId,
        string orderCode,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        long amountVnd = 29_000) => PaymentOrder.CreateVipPurchase(
        orderId, _adminId, orderCode, amountVnd, VipPlan.InitialPlanId,
        PaymentOrder.VipPurchaseDurationHours,
        PaymentOrder.VipPurchaseDiscountPercent,
        $"key-{orderCode}", new string('f', 64), createdAt, expiresAt);

    private static ActionReceipt CreateReceipt(
        Guid userId,
        DateTimeOffset occurredAt,
        char fingerprint) => ActionReceipt.Create(
        Guid.NewGuid(), Guid.NewGuid(), "A04.Source", userId, Guid.NewGuid(),
        new string(fingerprint, 64), occurredAt, occurredAt);

    private static void EnqueueReading(
        IOutboxWriter writer,
        Guid userId,
        Guid sessionId,
        DateTimeOffset occurredAt) => writer.Enqueue(
        ReadingEventTypes.ChapterReadQualified,
        new ChapterReadQualified(
            sessionId, userId, Guid.NewGuid(), Guid.NewGuid(), occurredAt,
            "a04-policy-v1", 9000, 15_000, "a04-manifest-v1"),
        occurredAt);

    private void AddMissionObservations(
        AppDbContext db,
        DateTimeOffset inside,
        DateTimeOffset outside)
    {
        var definition = MissionDefinition.Create(
            Guid.NewGuid(), $"a04-{Guid.NewGuid():N}", "A04 KPI", string.Empty,
            MissionActionTypes.ChapterReadQualified, MissionCycleTypes.Daily,
            inside.AddDays(-1));
        var revision = MissionRevision.Create(
            Guid.NewGuid(), definition.Id, 1, 1, 25,
            MissionRepeatPolicies.UniqueSubjectPerPeriod, inside.AddDays(-1), null, "{}");
        db.Set<MissionDefinition>().Add(definition);
        db.Set<MissionRevision>().Add(revision);

        var grantedProgress = MissionProgress.Create(
            Guid.NewGuid(), _adminId, revision.Id, $"a04-granted-{Guid.NewGuid():N}");
        grantedProgress.Increment(1, inside.AddMinutes(2));
        var rewardId = Guid.NewGuid();
        var operation = WalletOperation.Create(
            Guid.NewGuid(), _adminId, WalletOperationTypes.MissionReward,
            WalletOperationSourceTypes.RewardGrant, rewardId, null, inside.AddMinutes(2));
        db.Set<MissionProgress>().Add(grantedProgress);
        db.Set<WalletOperation>().Add(operation);
        db.Set<WalletEntry>().Add(WalletEntry.Create(
            Guid.NewGuid(), operation, WalletCoinTypes.Bonus, WalletBuckets.Available,
            25, 1, inside.AddMinutes(2)));
        db.Set<MissionRewardGrant>().Add(MissionRewardGrant.Create(
            rewardId, grantedProgress.Id, _adminId, 25, MissionRewardOutcomes.Granted,
            AdministrationReportingHandler.GetVietnamDate(inside),
            inside.AddMinutes(2), operation.Id));

        var deniedProgress = MissionProgress.Create(
            Guid.NewGuid(), _adminId, revision.Id, $"a04-denied-{Guid.NewGuid():N}");
        deniedProgress.Increment(1, inside.AddMinutes(3));
        db.Set<MissionProgress>().Add(deniedProgress);
        db.Set<MissionRewardGrant>().Add(MissionRewardGrant.Create(
            Guid.NewGuid(), deniedProgress.Id, _adminId, 40,
            MissionRewardOutcomes.DeniedDailyCap,
            AdministrationReportingHandler.GetVietnamDate(inside),
            inside.AddMinutes(3), null));

        var outsideProgress = MissionProgress.Create(
            Guid.NewGuid(), _adminId, revision.Id, $"a04-outside-{Guid.NewGuid():N}");
        outsideProgress.Increment(1, outside);
        db.Set<MissionProgress>().Add(outsideProgress);
        db.Set<MissionRewardGrant>().Add(MissionRewardGrant.Create(
            Guid.NewGuid(), outsideProgress.Id, _adminId, 50,
            MissionRewardOutcomes.DeniedDailyCap,
            AdministrationReportingHandler.GetVietnamDate(outside), outside, null));
    }

    private void AddTopUpLedger(AppDbContext db, Guid orderId, DateTimeOffset occurredAt)
    {
        var operation = WalletOperation.Create(
            Guid.NewGuid(), _adminId, WalletOperationTypes.TopUpCredit,
            WalletOperationSourceTypes.PaymentOrder, orderId, null, occurredAt);
        db.Set<WalletOperation>().Add(operation);
        db.Set<WalletEntry>().Add(WalletEntry.Create(
            Guid.NewGuid(), operation, WalletCoinTypes.Paid, WalletBuckets.Available,
            10_000, 1, occurredAt));
    }

    private async Task CompleteRefundAsync(Guid refundId)
    {
        RefundAdministrationResult reviewed;
        await using (var scope = _services.CreateAsyncScope())
            reviewed = await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
                .ReviewAsync(new(refundId, _adminId, $"a04-review-{Guid.NewGuid():N}"));
        Assert.Equal(RefundAdministrationOutcome.Succeeded, reviewed.Outcome);
        RefundAdministrationResult approved;
        await using (var scope = _services.CreateAsyncScope())
            approved = await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
                .ApproveAsync(new(
                    refundId, _adminId, reviewed.State!.Version, "A04 verified original lot",
                    $"a04-approve-{Guid.NewGuid():N}", new string('1', 64),
                    $"a04-approve-correlation-{Guid.NewGuid():N}"));
        Assert.Equal(RefundAdministrationOutcome.Succeeded, approved.Outcome);
        RefundAdministrationResult transferred;
        await using (var scope = _services.CreateAsyncScope())
            transferred = await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
                .RecordTransferAsync(new(
                    refundId, _adminId, approved.State!.Version,
                    $"A04-BANK-{Guid.NewGuid():N}", "evidence://a04-test",
                    RefundTransferReportedOutcomes.Succeeded,
                    $"a04-transfer-{Guid.NewGuid():N}", new string('2', 64),
                    $"a04-transfer-correlation-{Guid.NewGuid():N}"));
        Assert.Equal(RefundAdministrationOutcome.Succeeded, transferred.Outcome);
        RefundAdministrationResult completed;
        await using (var scope = _services.CreateAsyncScope())
            completed = await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
                .ResolveTransferAsync(new(
                    refundId, _adminId, transferred.State!.Version,
                    transferred.State.TransferRecordId!.Value,
                    "A04 transfer independently verified",
                    $"a04-complete-{Guid.NewGuid():N}", new string('3', 64), true,
                    $"a04-complete-correlation-{Guid.NewGuid():N}"));
        Assert.Equal(RefundAdministrationOutcome.Succeeded, completed.Outcome);
    }

    private void AddFulfilledTopUp(
        AppDbContext db,
        Guid orderId,
        string orderCode,
        long amountVnd,
        DateTimeOffset paidAt,
        Guid? userId = null)
    {
        var order = PaymentOrder.CreateTopUp(
            orderId,
            userId ?? _adminId,
            orderCode,
            amountVnd,
            TopUpAmountPolicy.CalculatePaidCoins(amountVnd),
            $"key-{orderCode}",
            new string(orderCode.EndsWith("LATE", StringComparison.Ordinal) ? 'b' : 'a', 64),
            paidAt.AddMinutes(-1),
            paidAt.AddMinutes(29));
        order.FulfillTopUp(paidAt);
        db.Set<PaymentOrder>().Add(order);
        db.Set<PaidCoinLot>().Add(PaidCoinLot.Create(
            Guid.NewGuid(), userId ?? _adminId, order.Id,
            TopUpAmountPolicy.CalculatePaidCoins(amountVnd), paidAt));
    }

    private sealed class ReportingGateObserver : IAdministrationReportingReadObserver
    {
        private bool _dashboardArmed;
        private bool _auditArmed;
        private readonly TaskCompletionSource _dashboardRelease = NewSource();
        private readonly TaskCompletionSource _auditRelease = NewSource();
        internal TaskCompletionSource DashboardReached { get; } = NewSource();
        internal TaskCompletionSource AuditReached { get; } = NewSource();

        internal void ArmDashboard() => _dashboardArmed = true;
        internal void ArmAudit() => _auditArmed = true;
        internal void ReleaseDashboard() => _dashboardRelease.TrySetResult();
        internal void ReleaseAudit() => _auditRelease.TrySetResult();

        public async Task AfterDashboardSnapshotEstablishedAsync(CancellationToken cancellationToken)
        {
            if (!_dashboardArmed) return;
            DashboardReached.TrySetResult();
            await _dashboardRelease.Task.WaitAsync(cancellationToken);
        }

        public async Task AfterAuditSnapshotEstablishedAsync(CancellationToken cancellationToken)
        {
            if (!_auditArmed) return;
            AuditReached.TrySetResult();
            await _auditRelease.Task.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class A04RefundRecipientProtector : IRefundRecipientProtector
    {
        public ProtectedRefundRecipient Protect(string accountNumber, string accountHolder) =>
            new([1, 2, 3], [4, 5, 6], "v1");

        public UnprotectedRefundRecipient Unprotect(
            byte[] accountNumberCiphertext,
            byte[] accountHolderCiphertext,
            string protectionVersion)
        {
            if (!accountNumberCiphertext.SequenceEqual(new byte[] { 1, 2, 3 }) ||
                !accountHolderCiphertext.SequenceEqual(new byte[] { 4, 5, 6 }) ||
                protectionVersion != "v1")
                throw new InvalidOperationException("A04 protected recipient fixture is invalid.");
            return new("0123456789", "NGUYEN VAN A");
        }
    }
}
