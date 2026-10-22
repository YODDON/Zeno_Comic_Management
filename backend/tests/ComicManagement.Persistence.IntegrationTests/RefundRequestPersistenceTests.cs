using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Events;
using ComicManagement.Billing.Refunds;
using ComicManagement.Identity.Users;
using ComicManagement.Persistence;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Reservations;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class RefundRequestPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_refund_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
    private readonly BlockingRefundObserver _observer = new();
    private readonly BlockingRefundApprovalObserver _approvalObserver = new();
    private readonly BlockingRefundPayoutObserver _payoutObserver = new();
    private readonly TestFingerprintService _fingerprints = new();
    private ServiceProvider _services = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var services = new ServiceCollection();
        services.AddPersistence(_postgres.GetConnectionString());
        services.RemoveAll<IRefundRequestCriticalSectionObserver>();
        services.AddSingleton<IRefundRequestCriticalSectionObserver>(_observer);
        services.RemoveAll<IRefundApprovalCriticalSectionObserver>();
        services.AddSingleton<IRefundApprovalCriticalSectionObserver>(_approvalObserver);
        services.RemoveAll<IRefundPayoutCriticalSectionObserver>();
        services.AddSingleton<IRefundPayoutCriticalSectionObserver>(_payoutObserver);
        services.AddSingleton<IRefundRecipientProtector, TestRecipientProtector>();
        services.AddSingleton<IRefundRequestFingerprintService>(_fingerprints);
        _services = services.BuildServiceProvider(validateScopes: true);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();
        _userId = Guid.NewGuid();
        context.Users.Add(User.CreateFromGoogle(
            _userId,
            "Refund Test User",
            null,
            null,
            DateTimeOffset.UtcNow.AddDays(-30)));
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Admin_approval_atomically_reserves_exact_original_lot_and_replays()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf02-approve", "duplicate transfer");
        var refundId = created.State!.RefundId;
        var review = await ReviewAsync(refundId);

        var approved = await ApproveAsync(
            refundId, review.State!.Version, "Original lot fully verified");
        var replay = await ApproveAsync(
            refundId, review.State.Version, "Original lot fully verified");

        Assert.Equal(RefundAdministrationOutcome.Succeeded, approved.Outcome);
        Assert.Equal(RefundRequestStatuses.RecoveryReserved, approved.State!.Status);
        Assert.Equal(approved.State.ReservationId, replay.State!.ReservationId);
        Assert.Equal(approved.State.WalletOperationId, replay.State.WalletOperationId);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking().SingleAsync();
        var lot = await context.PaidCoinLots.AsNoTracking()
            .SingleAsync(candidate => candidate.TopUpOrderId == orderId);
        var reservation = await context.CoinReservations.AsNoTracking().SingleAsync();
        var operation = await context.WalletOperations.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == reservation.WalletOperationId);
        var entries = await context.WalletEntries.AsNoTracking()
            .Where(entry => entry.OperationId == operation.Id)
            .OrderBy(entry => entry.LegOrder)
            .ToArrayAsync();

        Assert.Equal(0, wallet.PaidAvailable);
        Assert.Equal(10_000, wallet.PaidReserved);
        Assert.Equal(10_000, lot.ReservedCoins);
        Assert.Equal(CoinReservationStatuses.Reserved, reservation.Status);
        Assert.Equal(WalletOperationTypes.RefundReserve, operation.OperationType);
        Assert.Equal(WalletOperationSourceTypes.RefundRequest, operation.SourceType);
        Assert.Equal(refundId, operation.SourceId);
        Assert.Equal([-10_000L, 10_000L], entries.Select(entry => entry.DeltaCoins));
        Assert.Single(await context.CoinReservations.ToListAsync());
        Assert.Single(await context.WalletOperations
            .Where(candidate => candidate.OperationType == WalletOperationTypes.RefundReserve)
            .ToListAsync());
        Assert.Contains(await context.AuditLogs.AsNoTracking().ToListAsync(),
            audit => audit.Action == "Billing.RefundRecoveryReserved" &&
                     audit.TargetId == refundId);
        var outbox = await context.OutboxMessages.AsNoTracking().SingleAsync(message =>
            message.EventType == BillingEventTypes.RefundRecoveryReserved);
        using (var payload = JsonDocument.Parse(outbox.Payload))
        {
            var root = payload.RootElement;
            Assert.Equal(refundId, root.GetProperty("refundRequestId").GetGuid());
            Assert.Equal(_userId, root.GetProperty("userId").GetGuid());
            Assert.Equal(orderId, root.GetProperty("paymentOrderId").GetGuid());
            Assert.Equal(lot.Id, root.GetProperty("paidCoinLotId").GetGuid());
            Assert.Equal(operation.Id, root.GetProperty("walletOperationId").GetGuid());
            Assert.Equal(10_000, root.GetProperty("reservedCoins").GetInt64());
            Assert.True(root.TryGetProperty("reservedAt", out _));
            Assert.DoesNotContain("account", outbox.Payload, StringComparison.OrdinalIgnoreCase);
        }
        await AssertImmutableViolationAsync(
            $"UPDATE wallet.coin_reservations SET amount_coins = 9999 " +
            $"WHERE id = '{reservation.Id:D}'");
        await AssertImmutableViolationAsync(
            $"DELETE FROM wallet.coin_reservations WHERE id = '{reservation.Id:D}'");
    }

    [Fact]
    public async Task Verified_transfer_completion_consumes_reservation_once_and_replays()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf03-complete", "duplicate transfer");
        var reviewed = await ReviewAsync(created.State!.RefundId);
        var approved = await ApproveAsync(created.State.RefundId, reviewed.State!.Version,
            "Original lot verified", "rf03-approval-complete");
        var transfer = await RecordTransferAsync(created.State.RefundId,
            approved.State!.Version, RefundTransferReportedOutcomes.Succeeded,
            "BANK-COMPLETE", "rf03-transfer-complete");

        var completed = await ResolveTransferAsync(created.State.RefundId,
            transfer.State!.Version, transfer.State.TransferRecordId!.Value, true,
            "rf03-complete-key");
        var replay = await ResolveTransferAsync(created.State.RefundId,
            transfer.State.Version, transfer.State.TransferRecordId.Value, true,
            "rf03-complete-key");

        Assert.Equal(RefundRequestStatuses.Completed, completed.State!.Status);
        Assert.Equal(completed.State.ResolutionOperationId, replay.State!.ResolutionOperationId);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking().SingleAsync();
        var lot = await context.PaidCoinLots.AsNoTracking()
            .SingleAsync(item => item.TopUpOrderId == orderId);
        var reservation = await context.CoinReservations.AsNoTracking().SingleAsync();
        Assert.Equal(0, wallet.PaidReserved);
        Assert.Equal(10_000, lot.RefundedCoins);
        Assert.Equal(0, lot.ReservedCoins);
        Assert.Equal(CoinReservationStatuses.Consumed, reservation.Status);
        Assert.Single(await context.WalletOperations.Where(item =>
            item.OperationType == WalletOperationTypes.RefundComplete).ToListAsync());
        Assert.Single(await context.OutboxMessages.Where(item =>
            item.EventType == BillingEventTypes.RefundCompleted).ToListAsync());
        await AssertImmutableViolationAsync(
            $"UPDATE billing.refund_transfer_records SET bank_reference='OTHER' " +
            $"WHERE id='{completed.State.TransferRecordId!.Value:D}'");
        await AssertImmutableViolationAsync(
            $"DELETE FROM billing.refund_transfer_records " +
            $"WHERE id='{completed.State.TransferRecordId!.Value:D}'");
    }

    [Fact]
    public async Task Confirmed_failed_transfer_releases_original_reservation_atomically()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf03-release", "duplicate transfer");
        var reviewed = await ReviewAsync(created.State!.RefundId);
        var approved = await ApproveAsync(created.State.RefundId, reviewed.State!.Version,
            "Original lot verified", "rf03-approval-release");
        var transfer = await RecordTransferAsync(created.State.RefundId,
            approved.State!.Version, RefundTransferReportedOutcomes.Failed,
            "BANK-FAILED", "rf03-transfer-failed");

        var released = await ResolveTransferAsync(created.State.RefundId,
            transfer.State!.Version, transfer.State.TransferRecordId!.Value, false,
            "rf03-release-key");

        Assert.Equal(RefundRequestStatuses.ReservationReleased, released.State!.Status);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await context.Wallets.AsNoTracking().SingleAsync();
        var lot = await context.PaidCoinLots.AsNoTracking()
            .SingleAsync(item => item.TopUpOrderId == orderId);
        Assert.Equal(10_000, wallet.PaidAvailable);
        Assert.Equal(0, wallet.PaidReserved);
        Assert.Equal(0, lot.RefundedCoins);
        Assert.Equal(0, lot.ReservedCoins);
        Assert.Equal(CoinReservationStatuses.Released,
            (await context.CoinReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await context.OutboxMessages.Where(item =>
            item.EventType == BillingEventTypes.RefundReservationReleased).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_exact_completion_contends_and_creates_one_financial_resolution()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf03-complete-race", "duplicate transfer");
        var reviewed = await ReviewAsync(created.State!.RefundId);
        var approved = await ApproveAsync(created.State.RefundId, reviewed.State!.Version,
            "Original lot verified", "rf03-approval-race");
        var transfer = await RecordTransferAsync(created.State.RefundId,
            approved.State!.Version, RefundTransferReportedOutcomes.Succeeded,
            "BANK-RACE", "rf03-transfer-race");
        var release = _payoutObserver.BlockNext();

        var first = ResolveTransferAsync(created.State.RefundId, transfer.State!.Version,
            transfer.State.TransferRecordId!.Value, true, "rf03-complete-race-key");
        await _payoutObserver.WaitUntilBlockedAsync();
        var second = ResolveTransferAsync(created.State.RefundId, transfer.State.Version,
            transfer.State.TransferRecordId.Value, true, "rf03-complete-race-key");
        await WaitForDatabaseLockWaiterAsync();
        release();

        var results = await Task.WhenAll(first, second);
        Assert.All(results, result =>
            Assert.Equal(RefundAdministrationOutcome.Succeeded, result.Outcome));
        Assert.Equal(results[0].State!.ResolutionOperationId,
            results[1].State!.ResolutionOperationId);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.WalletOperations.CountAsync(item =>
            item.OperationType == WalletOperationTypes.RefundComplete));
        Assert.Equal(1, await context.OutboxMessages.CountAsync(item =>
            item.EventType == BillingEventTypes.RefundCompleted));
    }

    [Fact]
    public async Task Completion_integrity_drift_rolls_back_without_partial_financial_state()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf03-drift", "duplicate transfer");
        var reviewed = await ReviewAsync(created.State!.RefundId);
        var approved = await ApproveAsync(created.State.RefundId, reviewed.State!.Version,
            "Original lot verified", "rf03-approval-drift");
        var transfer = await RecordTransferAsync(created.State.RefundId,
            approved.State!.Version, RefundTransferReportedOutcomes.Succeeded,
            "BANK-DRIFT", "rf03-transfer-drift");
        await CorruptRecipientForTestAsync(created.State.RefundId, "ciphertext");

        var result = await ResolveTransferAsync(created.State.RefundId,
            transfer.State!.Version, transfer.State.TransferRecordId!.Value, true,
            "rf03-complete-drift");

        Assert.Equal(RefundAdministrationOutcome.IntegrityConflict, result.Outcome);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(RefundRequestStatuses.TransferPending,
            (await context.RefundRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(CoinReservationStatuses.Reserved,
            (await context.CoinReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await context.WalletOperations.Where(item =>
            item.OperationType == WalletOperationTypes.RefundComplete).ToListAsync());
        Assert.Empty(await context.OutboxMessages.Where(item =>
            item.EventType == BillingEventTypes.RefundCompleted).ToListAsync());
    }

    [Fact]
    public async Task Unknown_transfer_holds_reservation_until_explicit_reconciliation()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf03-unknown", "duplicate transfer");
        var reviewed = await ReviewAsync(created.State!.RefundId);
        var approved = await ApproveAsync(created.State.RefundId, reviewed.State!.Version,
            "Original lot verified", "rf03-approval-unknown");
        var transfer = await RecordTransferAsync(created.State.RefundId,
            approved.State!.Version, RefundTransferReportedOutcomes.Unknown,
            "BANK-UNKNOWN", "rf03-transfer-unknown");

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(10_000, (await context.Wallets.AsNoTracking().SingleAsync()).PaidReserved);
        Assert.Equal(CoinReservationStatuses.Reserved,
            (await context.CoinReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await context.WalletOperations.Where(item =>
            item.OperationType == WalletOperationTypes.RefundComplete ||
            item.OperationType == WalletOperationTypes.RefundRelease).ToListAsync());

        var result = await ResolveTransferAsync(created.State.RefundId,
            transfer.State!.Version, transfer.State.TransferRecordId!.Value, true,
            "rf03-unknown-complete");
        Assert.Equal(RefundAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(RefundRequestStatuses.Completed, result.State!.Status);
    }

    [Fact]
    public async Task Concurrent_exact_approval_really_contends_and_reserves_once()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf02-race", "duplicate transfer");
        var review = await ReviewAsync(created.State!.RefundId);
        var release = _approvalObserver.BlockNext();

        var first = ApproveAsync(
            created.State.RefundId, review.State!.Version, "Verified");
        await _approvalObserver.WaitUntilBlockedAsync();
        var second = ApproveAsync(
            created.State.RefundId, review.State.Version, "Verified");
        await WaitForDatabaseLockWaiterAsync();
        release();

        var results = await Task.WhenAll(first, second);
        Assert.All(results, result =>
            Assert.Equal(RefundAdministrationOutcome.Succeeded, result.Outcome));
        Assert.Equal(results[0].State!.ReservationId, results[1].State!.ReservationId);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.CoinReservations.CountAsync());
        Assert.Equal(2, await context.WalletEntries.CountAsync(entry =>
            entry.OperationId == results[0].State!.WalletOperationId));
    }

    [Fact]
    public async Task Concurrent_different_approval_keys_contend_and_only_one_reserves()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf02-race-different", "duplicate transfer");
        var review = await ReviewAsync(created.State!.RefundId);
        var release = _approvalObserver.BlockNext();

        var first = ApproveAsync(
            created.State.RefundId, review.State!.Version, "Verified", "approval-a");
        await _approvalObserver.WaitUntilBlockedAsync();
        var second = ApproveAsync(
            created.State.RefundId, review.State.Version, "Verified", "approval-b");
        await WaitForDatabaseLockWaiterAsync();
        release();

        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result =>
            result.Outcome == RefundAdministrationOutcome.Succeeded);
        Assert.Single(results, result =>
            result.Outcome == RefundAdministrationOutcome.StateConflict);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.CoinReservations.CountAsync());
        Assert.Equal(1, await context.OutboxMessages.CountAsync(message =>
            message.EventType == BillingEventTypes.RefundRecoveryReserved));
    }

    [Fact]
    public async Task Approval_replay_uses_immutable_result_after_future_lifecycle_transitions()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf02-future-replay", "duplicate transfer");
        var review = await ReviewAsync(created.State!.RefundId);
        var approved = await ApproveAsync(
            created.State.RefundId, review.State!.Version, "Verified");

        await SimulateFutureConsumedStateAsync(created.State.RefundId);
        var consumedReplay = await ApproveAsync(
            created.State.RefundId, review.State.Version, "Verified");

        await SimulateFutureReservationStateAsync(
            created.State.RefundId,
            CoinReservationStatuses.Released,
            "ReservationReleased");
        var releasedReplay = await ApproveAsync(
            created.State.RefundId, review.State.Version, "Verified");

        Assert.Equal(RefundAdministrationOutcome.Succeeded, consumedReplay.Outcome);
        Assert.Equal(RefundAdministrationOutcome.Succeeded, releasedReplay.Outcome);
        Assert.Equal(RefundRequestStatuses.RecoveryReserved, consumedReplay.State!.Status);
        Assert.Equal(approved.State!.Version, consumedReplay.State.Version);
        Assert.Equal(approved.State.ReservationId, releasedReplay.State!.ReservationId);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.CoinReservations.CountAsync());
        Assert.Equal(1, await context.WalletOperations.CountAsync(operation =>
            operation.OperationType == WalletOperationTypes.RefundReserve));
        Assert.Equal(2, await context.WalletEntries.CountAsync(entry =>
            entry.OperationId == approved.State.WalletOperationId));
        Assert.Equal(1, await context.OutboxMessages.CountAsync(message =>
            message.EventType == BillingEventTypes.RefundRecoveryReserved));
        Assert.Equal(1, await context.AuditLogs.CountAsync(audit =>
            audit.Action == "Billing.RefundRecoveryReserved"));
    }

    [Fact]
    public async Task Approval_waiting_on_wallet_lock_revalidates_lot_and_leaves_no_partial_state()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf02-drift", "duplicate transfer");
        var review = await ReviewAsync(created.State!.RefundId);

        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT user_id FROM wallet.wallets WHERE user_id = @id FOR UPDATE",
                         blocker,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue("id", _userId);
            await lockCommand.ExecuteScalarAsync();
        }

        var approval = ApproveAsync(
            created.State.RefundId, review.State!.Version, "Verified before drift");
        await WaitForDatabaseLockWaiterAsync();
        await using (var drift = new NpgsqlCommand(
                         "UPDATE wallet.paid_coin_lots SET spent_coins = 1, version = version + 1 " +
                         "WHERE topup_order_id = @id",
                         blocker,
                         transaction))
        {
            drift.Parameters.AddWithValue("id", orderId);
            Assert.Equal(1, await drift.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();

        var result = await approval;
        Assert.Equal(RefundAdministrationOutcome.RecoveryInsufficient, result.Outcome);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.CoinReservations.AsNoTracking().ToListAsync());
        Assert.Empty(await context.WalletOperations.AsNoTracking().Where(operation =>
            operation.OperationType == WalletOperationTypes.RefundReserve).ToListAsync());
        Assert.Equal(RefundRequestStatuses.UnderReview,
            (await context.RefundRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("unknown-version")]
    [InlineData("semantic-mismatch")]
    public async Task Approval_fails_closed_when_protected_recipient_cannot_be_trusted(
        string corruption)
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(
            orderId, $"rf02-recipient-{corruption}", "duplicate transfer");
        var review = await ReviewAsync(created.State!.RefundId);
        await CorruptRecipientForTestAsync(created.State.RefundId, corruption);

        var result = await ApproveAsync(
            created.State.RefundId, review.State!.Version, "Verified");

        Assert.Equal(RefundAdministrationOutcome.IntegrityConflict, result.Outcome);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.CoinReservations.ToListAsync());
        Assert.Empty(await context.WalletOperations.Where(operation =>
            operation.OperationType == WalletOperationTypes.RefundReserve).ToListAsync());
        Assert.Empty(await context.OutboxMessages.Where(message =>
            message.EventType == BillingEventTypes.RefundRecoveryReserved).ToListAsync());
        Assert.Equal(RefundRequestStatuses.UnderReview,
            (await context.RefundRequests.AsNoTracking().SingleAsync(request =>
                request.Id == created.State.RefundId)).Status);
    }

    [Fact]
    public async Task Admin_rejection_is_audited_and_never_mutates_financial_state()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var created = await CreateAsync(orderId, "rf02-reject", "duplicate transfer");
        var review = await ReviewAsync(created.State!.RefundId);
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
            .RejectAsync(new RejectRefundStoreRequest(
                created.State.RefundId,
                _userId,
                review.State!.Version,
                "Evidence does not satisfy policy",
                $"reject-{Guid.NewGuid():N}"));

        Assert.Equal(RefundAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(RefundRequestStatuses.Rejected, result.State!.Status);
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.CoinReservations.AsNoTracking().ToListAsync());
        Assert.Empty(await context.WalletOperations.AsNoTracking().Where(operation =>
            operation.OperationType == WalletOperationTypes.RefundReserve).ToListAsync());
        Assert.Contains(await context.AuditLogs.AsNoTracking().ToListAsync(),
            audit => audit.Action == "Billing.RefundRejected" &&
                     audit.TargetId == created.State.RefundId);
    }

    [Fact]
    public async Task PostgreSql_rejects_reservation_bound_to_wrong_wallet_operation_type()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(orderId, "rf02-integrity", "duplicate transfer");
        var review = await ReviewAsync(created.State!.RefundId);
        Guid lotId;
        await using (var scope = _services.CreateAsyncScope())
        {
            lotId = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PaidCoinLots.AsNoTracking()
                .Where(lot => lot.TopUpOrderId == orderId)
                .Select(lot => lot.Id)
                .SingleAsync();
        }

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var operationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await using (var command = new NpgsqlCommand(
                         """
                         INSERT INTO wallet.wallet_operations
                             (id,user_id,operation_type,source_type,source_id,idempotency_key,created_at)
                         VALUES (@operation,@user,'TopUpCredit','RefundRequest',@refund,NULL,clock_timestamp());
                         INSERT INTO wallet.wallet_entries
                             (id,operation_id,user_id,coin_type,bucket,delta_coins,leg_order,created_at)
                         VALUES
                             (@entry1,@operation,@user,'Paid','Available',-10000,1,clock_timestamp()),
                             (@entry2,@operation,@user,'Paid','Reserved',10000,2,clock_timestamp());
                         UPDATE billing.refund_requests
                         SET status='RecoveryReserved', approved_by=@user, approved_at=clock_timestamp(),
                             decision_reason='tampered', version=@next_version
                         WHERE id=@refund;
                         INSERT INTO wallet.coin_reservations
                             (id,refund_request_id,user_id,paid_coin_lot_id,wallet_operation_id,
                              approved_by,idempotency_key,request_fingerprint,
                              amount_coins,status,approval_result_status,
                              approval_result_version,approval_result_reviewed_by,
                              approval_result_reviewed_at,approval_result_decision_note,
                              created_at,resolved_at)
                         VALUES
                             (@reservation,@refund,@user,@lot,@operation,@user,'tampered-key',
                              @fingerprint,10000,'Reserved','RecoveryReserved',@next_version,
                              @reviewed_by,@reviewed_at,'tampered',
                              clock_timestamp(),NULL);
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("operation", operationId);
            command.Parameters.AddWithValue("user", _userId);
            command.Parameters.AddWithValue("refund", created.State.RefundId);
            command.Parameters.AddWithValue("entry1", Guid.NewGuid());
            command.Parameters.AddWithValue("entry2", Guid.NewGuid());
            command.Parameters.AddWithValue("next_version", review.State!.Version + 1);
            command.Parameters.AddWithValue("reviewed_by", review.State.ReviewedBy!.Value);
            command.Parameters.AddWithValue("reviewed_at", review.State.ReviewedAt!.Value);
            command.Parameters.AddWithValue("reservation", reservationId);
            command.Parameters.AddWithValue("lot", lotId);
            command.Parameters.AddWithValue("fingerprint", new string('d', 64));
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await verification.CoinReservations.AsNoTracking().Where(
            reservation => reservation.Id == reservationId).ToListAsync());
        Assert.Equal(
            RefundRequestStatuses.UnderReview,
            (await verification.RefundRequests.AsNoTracking().SingleAsync(
                request => request.Id == created.State.RefundId)).Status);
    }

    [Theory]
    [InlineData("missing-wallet")]
    [InlineData("missing-lot")]
    [InlineData("wrong-wallet")]
    [InlineData("wrong-lot")]
    [InlineData("wrong-ledger")]
    [InlineData("wrong-source")]
    public async Task PostgreSql_rejects_incomplete_or_mismatched_reservation_transactions(
        string corruption)
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await SeedWalletForOrderAsync(orderId);
        var created = await CreateAsync(
            orderId, $"rf02-db-guard-{corruption}", "duplicate transfer");
        var review = await ReviewAsync(created.State!.RefundId);
        Guid lotId;
        await using (var scope = _services.CreateAsyncScope())
        {
            lotId = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PaidCoinLots.AsNoTracking()
                .Where(lot => lot.TopUpOrderId == orderId)
                .Select(lot => lot.Id)
                .SingleAsync();
        }

        var walletAmount = corruption == "missing-wallet" ? 0 :
            corruption == "wrong-wallet" ? 9_999 : 10_000;
        var lotAmount = corruption == "missing-lot" ? 0 :
            corruption == "wrong-lot" ? 9_999 : 10_000;
        var ledgerAmount = corruption == "wrong-ledger" ? 9_999 : 10_000;
        var sourceType = corruption == "wrong-source" ?
            WalletOperationSourceTypes.PaymentOrder : WalletOperationSourceTypes.RefundRequest;

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var operationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        await using (var command = new NpgsqlCommand(
                         """
                         INSERT INTO wallet.wallet_operations
                             (id,user_id,operation_type,source_type,source_id,idempotency_key,created_at)
                         VALUES (@operation,@user,'RefundReserve',@source_type,@refund,NULL,clock_timestamp());
                         INSERT INTO wallet.wallet_entries
                             (id,operation_id,user_id,coin_type,bucket,delta_coins,leg_order,created_at)
                         VALUES
                             (@entry1,@operation,@user,'Paid','Available',-@ledger_amount,1,clock_timestamp()),
                             (@entry2,@operation,@user,'Paid','Reserved',@ledger_amount,2,clock_timestamp());
                         UPDATE wallet.wallets
                         SET paid_available = paid_available - @wallet_amount,
                             paid_reserved = paid_reserved + @wallet_amount,
                             version = version + 1,
                             updated_at = clock_timestamp()
                         WHERE user_id = @user;
                         UPDATE wallet.paid_coin_lots
                         SET reserved_coins = reserved_coins + @lot_amount,
                             version = version + 1
                         WHERE id = @lot;
                         UPDATE billing.refund_requests
                         SET status='RecoveryReserved', approved_by=@user, approved_at=clock_timestamp(),
                             decision_reason='tampered', version=@next_version
                         WHERE id=@refund;
                         INSERT INTO wallet.coin_reservations
                             (id,refund_request_id,user_id,paid_coin_lot_id,wallet_operation_id,
                              approved_by,idempotency_key,request_fingerprint,amount_coins,status,
                              approval_result_status,approval_result_version,
                              approval_result_reviewed_by,approval_result_reviewed_at,
                              approval_result_decision_note,created_at,resolved_at)
                         VALUES
                             (@reservation,@refund,@user,@lot,@operation,@user,@key,@fingerprint,
                              10000,'Reserved','RecoveryReserved',@next_version,@reviewed_by,
                              @reviewed_at,'tampered',clock_timestamp(),NULL);
                         INSERT INTO administration.audit_logs
                             (id,actor_id,action,target_type,target_id,correlation_id,
                              occurred_at,redacted_changes)
                         VALUES
                             (@audit,@user,'Billing.RefundRecoveryReserved','RefundRequest',
                              @refund,'negative-test',clock_timestamp(),'{}'::jsonb);
                         INSERT INTO infrastructure.outbox_messages
                             (id,event_type,payload,occurred_at,available_at,status,
                              attempt_count,locked_until,last_error_redacted)
                         VALUES
                             (@outbox,'Billing.RefundRecoveryReserved.v1','{}'::jsonb,
                              clock_timestamp(),clock_timestamp(),'Pending',0,NULL,NULL);
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("operation", operationId);
            command.Parameters.AddWithValue("user", _userId);
            command.Parameters.AddWithValue("source_type", sourceType);
            command.Parameters.AddWithValue("refund", created.State.RefundId);
            command.Parameters.AddWithValue("entry1", Guid.NewGuid());
            command.Parameters.AddWithValue("entry2", Guid.NewGuid());
            command.Parameters.AddWithValue("ledger_amount", ledgerAmount);
            command.Parameters.AddWithValue("wallet_amount", walletAmount);
            command.Parameters.AddWithValue("lot_amount", lotAmount);
            command.Parameters.AddWithValue("lot", lotId);
            command.Parameters.AddWithValue("next_version", review.State!.Version + 1);
            command.Parameters.AddWithValue("reviewed_by", review.State.ReviewedBy!.Value);
            command.Parameters.AddWithValue("reviewed_at", review.State.ReviewedAt!.Value);
            command.Parameters.AddWithValue("reservation", reservationId);
            command.Parameters.AddWithValue("key", $"tampered-{corruption}");
            command.Parameters.AddWithValue("fingerprint", new string('e', 64));
            command.Parameters.AddWithValue("audit", auditId);
            command.Parameters.AddWithValue("outbox", outboxId);
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verification.CoinReservations.AnyAsync(item => item.Id == reservationId));
        Assert.False(await verification.WalletOperations.AnyAsync(item => item.Id == operationId));
        Assert.False(await verification.WalletEntries.AnyAsync(item => item.OperationId == operationId));
        Assert.False(await verification.AuditLogs.AnyAsync(item => item.Id == auditId));
        Assert.False(await verification.OutboxMessages.AnyAsync(item => item.Id == outboxId));
        Assert.Equal(RefundRequestStatuses.UnderReview,
            (await verification.RefundRequests.AsNoTracking().SingleAsync(item =>
                item.Id == created.State.RefundId)).Status);
    }

    [Fact]
    public async Task Eligibility_uses_verified_paid_time_and_full_original_lot()
    {
        var eligibleOrder = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-6));
        var expiredOrder = await SeedFulfilledTopUpAsync(
            DateTimeOffset.UtcNow.AddDays(-RefundRequest.RequestWindowDays).AddMinutes(-1));

        var eligible = await EligibilityAsync(eligibleOrder);
        var expired = await EligibilityAsync(expiredOrder);

        Assert.Equal(RefundRequestOutcome.Succeeded, eligible.Outcome);
        Assert.True(eligible.State!.Eligible);
        Assert.Equal(100_000, eligible.State.OriginalAmountVnd);
        Assert.Equal(5_000, eligible.State.FeeVnd);
        Assert.Equal(95_000, eligible.State.NetRefundVnd);
        Assert.Equal(10_000, eligible.State.LotGrantedCoins);
        Assert.Equal(RefundRequestOutcome.WindowExpired, expired.Outcome);
        Assert.False(expired.State!.Eligible);
        Assert.Equal("REFUND_WINDOW_EXPIRED", expired.State.ReasonCode);
    }

    [Fact]
    public async Task Concurrent_same_key_requests_really_contend_and_create_one_request()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var release = _observer.BlockNext();
        var firstTask = CreateAsync(orderId, "same-key", "duplicate transfer");
        await _observer.WaitUntilBlockedAsync();
        var secondTask = CreateAsync(orderId, "same-key", "duplicate transfer");
        await WaitForAdvisoryWaiterAsync();
        release();

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(RefundRequestOutcome.Succeeded, result.Outcome));
        Assert.Equal(results[0].State!.RefundId, results[1].State!.RefundId);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.RefundRequests.CountAsync());
        Assert.Equal(1, await context.RefundRecipients.CountAsync());
        Assert.Empty(await context.WalletEntries.ToListAsync());
        Assert.Empty(await context.WalletOperations.ToListAsync());
    }

    [Fact]
    public async Task Request_waiting_on_order_lock_past_seven_day_boundary_fails_closed()
    {
        var initialServerNow = await GetDatabaseTimeAsync();
        var refundWindowBoundary = initialServerNow.AddSeconds(8);
        var paidAt = refundWindowBoundary.AddDays(-RefundRequest.RequestWindowDays);
        var orderId = await SeedFulfilledTopUpAsync(paidAt);
        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT id FROM billing.payment_orders WHERE id = @id FOR UPDATE",
                         blocker,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue("id", orderId);
            await lockCommand.ExecuteScalarAsync();
        }

        var beforeRequest = await GetDatabaseTimeAsync();
        Assert.True(
            beforeRequest < refundWindowBoundary,
            $"Precondition failed: PostgreSQL time {beforeRequest:O} was not before " +
            $"the refund boundary {refundWindowBoundary:O}.");

        var createTask = CreateAsync(orderId, "boundary-lock", "duplicate transfer");
        await WaitForDatabaseLockWaiterAsync();
        await WaitUntilDatabaseTimeAsync(refundWindowBoundary);
        await transaction.CommitAsync();

        var result = await createTask;
        Assert.Equal(RefundRequestOutcome.WindowExpired, result.Outcome);
        await AssertNoRefundStateAsync();
    }

    [Fact]
    public async Task Request_waiting_on_original_lot_revalidates_concurrent_spend_and_fails_closed()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT id FROM wallet.paid_coin_lots WHERE topup_order_id = @id FOR UPDATE",
                         blocker,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue("id", orderId);
            await lockCommand.ExecuteScalarAsync();
        }

        var createTask = CreateAsync(orderId, "lot-lock", "duplicate transfer");
        await WaitForDatabaseLockWaiterAsync();
        await using (var mutateCommand = new NpgsqlCommand(
                         """
                         UPDATE wallet.paid_coin_lots
                         SET spent_coins = 1, version = version + 1
                         WHERE topup_order_id = @id
                         """,
                         blocker,
                         transaction))
        {
            mutateCommand.Parameters.AddWithValue("id", orderId);
            Assert.Equal(1, await mutateCommand.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();

        var result = await createTask;
        Assert.Equal(RefundRequestOutcome.RecoveryInsufficient, result.Outcome);
        await AssertNoRefundStateAsync();
    }

    [Fact]
    public async Task Permanent_idempotency_conflicts_and_one_request_per_order_are_enforced()
    {
        var firstOrder = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var secondOrder = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var created = await CreateAsync(firstOrder, "refund-key", "duplicate transfer");
        _fingerprints.CurrentVersion = "v2";

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lot = await context.PaidCoinLots.SingleAsync(item => item.TopUpOrderId == firstOrder);
            lot.Spend(1);
            await context.SaveChangesAsync();
        }

        var replayAfterLotDrift = await CreateAsync(
            firstOrder, "refund-key", "duplicate transfer");
        var differentPayload = await CreateAsync(secondOrder, "refund-key", "other reason");
        var differentKeySameOrder = await CreateAsync(
            firstOrder, "different-key", "duplicate transfer");

        Assert.Equal(RefundRequestOutcome.Succeeded, created.Outcome);
        Assert.Equal(RefundRequestOutcome.Succeeded, replayAfterLotDrift.Outcome);
        Assert.Equal(created.State!.RefundId, replayAfterLotDrift.State!.RefundId);
        Assert.Equal(RefundRequestOutcome.IdempotencyConflict, differentPayload.Outcome);
        Assert.Equal(RefundRequestOutcome.RefundAlreadyExists, differentKeySameOrder.Outcome);
        await using var versionScope = _services.CreateAsyncScope();
        var persisted = await versionScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .RefundRequests.AsNoTracking().SingleAsync(item => item.Id == created.State.RefundId);
        Assert.Equal("v1", persisted.RequestFingerprintVersion);
    }

    [Fact]
    public async Task Spent_original_lot_is_ineligible_and_request_has_no_financial_mutation()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lot = await context.PaidCoinLots.SingleAsync(item => item.TopUpOrderId == orderId);
            lot.Spend(1);
            await context.SaveChangesAsync();
        }

        var eligibility = await EligibilityAsync(orderId);
        var create = await CreateAsync(orderId, "spent-lot", "duplicate transfer");

        Assert.Equal(RefundRequestOutcome.RecoveryInsufficient, eligibility.Outcome);
        Assert.Equal("REFUND_RECOVERY_INSUFFICIENT", eligibility.State!.ReasonCode);
        Assert.Equal(RefundRequestOutcome.RecoveryInsufficient, create.Outcome);
        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await verification.RefundRequests.ToListAsync());
        Assert.Empty(await verification.WalletEntries.ToListAsync());
    }

    [Fact]
    public async Task Application_and_PostgreSql_reject_refund_snapshot_mutation_and_deletion()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var created = await CreateAsync(orderId, "constraints", "duplicate transfer");
        var refundId = created.State!.RefundId;

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await context.RefundRequests.SingleAsync(item => item.Id == refundId);
            context.Entry(request).Property(nameof(RefundRequest.Reason)).CurrentValue = "tampered";
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

            context.ChangeTracker.Clear();
            var recipient = await context.RefundRecipients.SingleAsync(
                item => item.RefundRequestId == refundId);
            context.Entry(recipient).Property(nameof(RefundRecipient.BankCode)).CurrentValue = "ACB";
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }

        await AssertImmutableViolationAsync(
            $"UPDATE billing.refund_requests SET reason = 'tampered' WHERE id = '{refundId:D}'");
        await AssertImmutableViolationAsync(
            $"UPDATE billing.refund_recipients SET account_number_encrypted = '\\x01'::bytea WHERE refund_request_id = '{refundId:D}'");
        await AssertImmutableViolationAsync(
            $"DELETE FROM billing.refund_recipients WHERE refund_request_id = '{refundId:D}'");
        await AssertImmutableViolationAsync(
            $"DELETE FROM billing.refund_requests WHERE id = '{refundId:D}'");
    }

    [Fact]
    public async Task PostgreSql_rejects_cross_user_refund_snapshot()
    {
        var ownershipOrderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));

        var otherUserId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(User.CreateFromGoogle(
            otherUserId, "Other Refund User", null, null, DateTimeOffset.UtcNow.AddDays(-2)));
        await context.SaveChangesAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO billing.refund_requests
                    (id, payment_order_id, user_id, payment_order_type, reason,
                     original_amount_vnd, fee_amount_vnd, net_refund_vnd,
                     required_recovery_coins, status, idempotency_key,
                     request_fingerprint, request_fingerprint_version, requested_at, version)
                VALUES
                    ({Guid.NewGuid()}, {ownershipOrderId}, {otherUserId}, {PaymentOrderTypes.TopUp},
                     {'x'}, {100_000L}, {5_000L}, {95_000L}, {10_000L},
                     {RefundRequestStatuses.Requested}, {'k'}, {new string('a', 64)}, {"v1"},
                     {DateTimeOffset.UtcNow}, {1L})
                """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Financial_processing_loader_returns_authoritative_protected_state()
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var created = await CreateAsync(orderId, "financial-loader", "duplicate transfer");

        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IRefundFinancialProcessingLoader>()
            .LoadForFinancialProcessingAsync(created.State!.RefundId);

        Assert.Equal(RefundFinancialLoadOutcome.Succeeded, result.Outcome);
        Assert.Equal(_userId, result.State!.UserId);
        Assert.Equal(orderId, result.State.Request.PaymentOrderId);
        Assert.Equal(PaymentOrderTypes.TopUp, result.State.OrderType);
        Assert.Equal(100_000, result.State.OrderAmountVnd);
        Assert.Equal(10_000, result.State.PaidCoinsSnapshot);
        Assert.Equal(10_000, result.State.LotGrantedCoins);
        Assert.True(result.State.IsFullyRecoverable);
        Assert.Equal("test-v1", result.State.RecipientProtectionVersion);
        Assert.Equal("0123456789", result.State.Recipient.AccountNumber);
        Assert.Equal("NGUYEN VAN A", result.State.Recipient.AccountHolder);
    }

    [Theory]
    [InlineData("request-snapshot")]
    [InlineData("payment-order")]
    [InlineData("paid-lot")]
    public async Task Financial_processing_loader_fails_closed_on_authoritative_drift(
        string driftTarget)
    {
        var orderId = await SeedFulfilledTopUpAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var created = await CreateAsync(
            orderId,
            $"financial-drift-{driftTarget}",
            "duplicate transfer");

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();

        if (driftTarget == "request-snapshot")
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                ALTER TABLE billing.refund_requests DISABLE TRIGGER ALL;
                ALTER TABLE billing.refund_requests DROP CONSTRAINT
                    fk_refund_requests_topup_payment_order_snapshot;
                UPDATE billing.refund_requests
                SET original_amount_vnd = 200000,
                    fee_amount_vnd = 10000,
                    net_refund_vnd = 190000
                WHERE id = {created.State!.RefundId};
                """);
        }
        else if (driftTarget == "payment-order")
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                ALTER TABLE billing.refund_requests DROP CONSTRAINT
                    fk_refund_requests_topup_payment_order_snapshot;
                ALTER TABLE billing.payment_orders DISABLE TRIGGER ALL;
                UPDATE billing.payment_orders
                SET amount_vnd = 200000, paid_coins_snapshot = 20000
                WHERE id = {orderId};
                """);
        }
        else
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                ALTER TABLE billing.refund_requests DROP CONSTRAINT
                    fk_refund_requests_original_paid_coin_lot_snapshot;
                ALTER TABLE wallet.paid_coin_lots DISABLE TRIGGER ALL;
                UPDATE wallet.paid_coin_lots SET granted_coins = 9999
                WHERE topup_order_id = {orderId};
                """);
        }

        var result = await scope.ServiceProvider
            .GetRequiredService<IRefundFinancialProcessingLoader>()
            .LoadForFinancialProcessingAsync(created.State!.RefundId);

        Assert.Equal(RefundFinancialLoadOutcome.IntegrityConflict, result.Outcome);
        Assert.Null(result.State);
        await transaction.RollbackAsync();
    }

    private async Task<Guid> SeedFulfilledTopUpAsync(DateTimeOffset paidAt)
    {
        paidAt = paidAt.ToUniversalTime();
        var orderId = Guid.NewGuid();
        var order = PaymentOrder.CreateTopUp(
            orderId,
            _userId,
            $"ZENO-{orderId:N}",
            100_000,
            10_000,
            $"seed-{orderId:N}",
            new string('a', 64),
            paidAt.AddMinutes(-10),
            paidAt.AddMinutes(10));
        order.FulfillTopUp(paidAt);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.PaymentOrders.Add(order);
        context.PaidCoinLots.Add(PaidCoinLot.Create(
            Guid.NewGuid(), _userId, orderId, 10_000, paidAt));
        await context.SaveChangesAsync();
        return orderId;
    }

    private async Task SeedWalletForOrderAsync(Guid orderId)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await context.PaymentOrders.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == orderId);
        var lot = await context.PaidCoinLots.SingleAsync(
            candidate => candidate.TopUpOrderId == orderId);
        var occurredAt = order.FulfilledAt!.Value;
        var wallet = WalletAccount.Create(_userId, occurredAt);
        wallet.CreditPaid(lot.GrantedCoins, occurredAt);
        var operation = WalletOperation.Create(
            Guid.NewGuid(),
            _userId,
            WalletOperationTypes.TopUpCredit,
            WalletOperationSourceTypes.PaymentOrder,
            orderId,
            null,
            occurredAt);
        context.Wallets.Add(wallet);
        context.WalletOperations.Add(operation);
        context.WalletEntries.Add(WalletEntry.Create(
            Guid.NewGuid(),
            operation,
            WalletCoinTypes.Paid,
            WalletBuckets.Available,
            lot.GrantedCoins,
            1,
            occurredAt));
        await context.SaveChangesAsync();
    }

    private async Task<RefundAdministrationResult> ReviewAsync(Guid refundId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
            .ReviewAsync(new ReviewRefundStoreRequest(
                refundId, _userId, $"review-{Guid.NewGuid():N}"));
    }

    private async Task<RefundAdministrationResult> ApproveAsync(
        Guid refundId,
        long expectedVersion,
        string note,
        string idempotencyKey = "rf02-approval-key")
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
            .ApproveAsync(new ApproveRefundStoreRequest(
                refundId,
                _userId,
                expectedVersion,
                note,
                idempotencyKey,
                FingerprintApproval(refundId, expectedVersion, note),
                $"approve-{Guid.NewGuid():N}"));
    }

    private async Task<RefundAdministrationResult> RecordTransferAsync(
        Guid refundId,
        long expectedVersion,
        string outcome,
        string bankReference,
        string idempotencyKey)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
            .RecordTransferAsync(new RecordRefundTransferStoreRequest(
                refundId, _userId, expectedVersion, bankReference,
                "evidence://manual-proof", outcome, idempotencyKey,
                new string(outcome == RefundTransferReportedOutcomes.Unknown ? 'c' : 'b', 64),
                $"transfer-{Guid.NewGuid():N}"));
    }

    private async Task<RefundAdministrationResult> ResolveTransferAsync(
        Guid refundId,
        long expectedVersion,
        Guid transferRecordId,
        bool succeeded,
        string idempotencyKey)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRefundAdministrationStore>()
            .ResolveTransferAsync(new ResolveRefundTransferStoreRequest(
                refundId, _userId, expectedVersion, transferRecordId,
                "Evidence independently reviewed", idempotencyKey,
                new string(succeeded ? 'd' : 'e', 64), succeeded,
                $"resolve-{Guid.NewGuid():N}"));
    }

    private async Task SimulateFutureReservationStateAsync(
        Guid refundId,
        string reservationStatus,
        string requestStatus)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE wallet.coin_reservations
            SET status = @reservation_status,
                resolved_at = clock_timestamp()
            WHERE refund_request_id = @refund_id;
            UPDATE billing.refund_requests
            SET status = @request_status,
                completed_by = CASE WHEN @request_status IN ('Completed','ReservationReleased')
                                    THEN @user ELSE completed_by END,
                completed_at = CASE WHEN @request_status IN ('Completed','ReservationReleased')
                                    THEN clock_timestamp() ELSE completed_at END,
                version = version + 1
            WHERE id = @refund_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reservation_status", reservationStatus);
        command.Parameters.AddWithValue("request_status", requestStatus);
        command.Parameters.AddWithValue("refund_id", refundId);
        command.Parameters.AddWithValue("user", _userId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task SimulateFutureConsumedStateAsync(Guid refundId)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO wallet.wallet_operations
                (id,user_id,operation_type,source_type,source_id,idempotency_key,created_at)
            VALUES (@operation,@user,'RefundPayout','RefundRequest',@refund_id,NULL,
                    clock_timestamp());
            INSERT INTO wallet.wallet_entries
                (id,operation_id,user_id,coin_type,bucket,delta_coins,leg_order,created_at)
            VALUES (@entry,@operation,@user,'Paid','Reserved',-10000,1,
                    clock_timestamp());
            UPDATE wallet.wallets
            SET paid_reserved = paid_reserved - 10000,
                version = version + 1,
                updated_at = clock_timestamp()
            WHERE user_id = @user;
            UPDATE wallet.paid_coin_lots
            SET reserved_coins = reserved_coins - 10000,
                refunded_coins = refunded_coins + 10000,
                version = version + 1
            WHERE id = (SELECT paid_coin_lot_id FROM wallet.coin_reservations
                        WHERE refund_request_id = @refund_id);
            UPDATE wallet.coin_reservations
            SET status = 'Consumed', resolved_at = clock_timestamp()
            WHERE refund_request_id = @refund_id;
            UPDATE billing.refund_requests
            SET status = 'Completed', completed_by = @user,
                completed_at = clock_timestamp(), version = version + 1
            WHERE id = @refund_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("operation", Guid.NewGuid());
        command.Parameters.AddWithValue("entry", Guid.NewGuid());
        command.Parameters.AddWithValue("user", _userId);
        command.Parameters.AddWithValue("refund_id", refundId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task CorruptRecipientForTestAsync(Guid refundId, string corruption)
    {
        var mutation = corruption switch
        {
            "ciphertext" => "account_number_encrypted = '\\x09'::bytea",
            "unknown-version" => "encryption_key_version = 'missing-key'",
            "semantic-mismatch" => "account_number_masked = '********0000'",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            ALTER TABLE billing.refund_recipients
                DISABLE TRIGGER trg_refund_recipients_immutable;
            UPDATE billing.refund_recipients SET {mutation}
            WHERE refund_request_id = @refund_id;
            ALTER TABLE billing.refund_recipients
                ENABLE TRIGGER trg_refund_recipients_immutable;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("refund_id", refundId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static string FingerprintApproval(
        Guid refundId,
        long expectedVersion,
        string note)
    {
        var canonical = FormattableString.Invariant(
            $"RefundApproval|refund={refundId:N}|version={expectedVersion}|note={note.Length}:{note}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private async Task<RefundEligibilityResult> EligibilityAsync(Guid orderId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RefundRequestHandler>()
            .GetEligibilityAsync(_userId, orderId);
    }

    private async Task<RefundRequestResult> CreateAsync(
        Guid orderId,
        string key,
        string reason)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RefundRequestHandler>()
            .CreateAsync(
                _userId,
                orderId,
                reason,
                new RefundRecipientInput("VCB", "0123456789", "NGUYEN VAN A"),
                key);
    }

    private async Task WaitForAdvisoryWaiterAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event = 'advisory'", connection);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The duplicate refund request did not contend on the advisory lock.");
    }

    private async Task WaitForDatabaseLockWaiterAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock'",
                connection);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The refund request did not contend on the authoritative row lock.");
    }

    private async Task<DateTimeOffset> GetDatabaseTimeAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        var value = await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL clock returned null.");
        return value switch
        {
            DateTimeOffset instant => instant.ToUniversalTime(),
            DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                $"Unexpected PostgreSQL clock type {value.GetType().FullName}."),
        };
    }

    private async Task WaitUntilDatabaseTimeAsync(DateTimeOffset boundary)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < timeout)
        {
            if (await GetDatabaseTimeAsync() >= boundary)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"PostgreSQL clock did not reach refund boundary {boundary:O}.");
    }

    private async Task AssertNoRefundStateAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.RefundRequests.AsNoTracking().ToListAsync());
        Assert.Empty(await context.RefundRecipients.AsNoTracking().ToListAsync());
    }

    private async Task AssertImmutableViolationAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
    }

    private async Task AssertForeignKeyViolationAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    private sealed class TestRecipientProtector : IRefundRecipientProtector
    {
        public ProtectedRefundRecipient Protect(string accountNumber, string accountHolder) =>
            new([1, 2, 3], [4, 5, 6], "test-v1");

        public UnprotectedRefundRecipient Unprotect(
            byte[] accountNumberCiphertext,
            byte[] accountHolderCiphertext,
            string protectionVersion)
        {
            if (!accountNumberCiphertext.SequenceEqual(new byte[] { 1, 2, 3 }) ||
                !accountHolderCiphertext.SequenceEqual(new byte[] { 4, 5, 6 }) ||
                protectionVersion != "test-v1")
            {
                throw new CryptographicException("Protected recipient is invalid.");
            }

            return new UnprotectedRefundRecipient("0123456789", "NGUYEN VAN A");
        }
    }

    private sealed class TestFingerprintService : IRefundRequestFingerprintService
    {
        public string CurrentVersion { get; set; } = "v1";

        public RefundRequestFingerprints CreateFingerprints(string canonicalRequest)
        {
            var candidates = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["v1"] = Fingerprint(canonicalRequest, "rf01-persistence-test-key-v1"),
                ["v2"] = Fingerprint(canonicalRequest, "rf01-persistence-test-key-v2"),
            };
            return new RefundRequestFingerprints(
                CurrentVersion,
                candidates[CurrentVersion],
                candidates);
        }

        private static string Fingerprint(string canonicalRequest, string key) => Convert.ToHexString(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(key),
                    Encoding.UTF8.GetBytes(canonicalRequest)))
            .ToLowerInvariant();
    }

    private sealed class BlockingRefundObserver : IRefundRequestCriticalSectionObserver
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _blocked;
        private TaskCompletionSource? _release;

        public Action BlockNext()
        {
            lock (_sync)
            {
                _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return () => _release.TrySetResult();
            }
        }

        public async Task WaitUntilBlockedAsync()
        {
            Task blocked;
            lock (_sync)
            {
                blocked = _blocked?.Task ?? throw new InvalidOperationException();
            }

            await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task AfterIdempotencyLockAcquiredAsync(
            Guid userId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Task? release = null;
            lock (_sync)
            {
                if (_blocked is not null && !_blocked.Task.IsCompleted)
                {
                    _blocked.TrySetResult();
                    release = _release!.Task;
                }
            }

            if (release is not null)
            {
                await release.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class BlockingRefundApprovalObserver : IRefundApprovalCriticalSectionObserver
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _blocked;
        private TaskCompletionSource? _release;

        public Action BlockNext()
        {
            lock (_sync)
            {
                _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return () => _release.TrySetResult();
            }
        }

        public async Task WaitUntilBlockedAsync()
        {
            Task blocked;
            lock (_sync)
            {
                blocked = _blocked?.Task ?? throw new InvalidOperationException();
            }

            await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task AfterFinancialLocksAcquiredAsync(
            Guid refundRequestId,
            CancellationToken cancellationToken)
        {
            Task? release = null;
            lock (_sync)
            {
                if (_blocked is not null && !_blocked.Task.IsCompleted)
                {
                    _blocked.TrySetResult();
                    release = _release!.Task;
                }
            }

            if (release is not null)
            {
                await release.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class BlockingRefundPayoutObserver : IRefundPayoutCriticalSectionObserver
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _blocked;
        private TaskCompletionSource? _release;

        public Action BlockNext()
        {
            lock (_sync)
            {
                _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return () => _release.TrySetResult();
            }
        }

        public async Task WaitUntilBlockedAsync()
        {
            Task blocked;
            lock (_sync)
            {
                blocked = _blocked?.Task ?? throw new InvalidOperationException();
            }
            await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task AfterFinancialLocksAcquiredAsync(Guid refundRequestId,
            CancellationToken cancellationToken)
        {
            Task? release = null;
            lock (_sync)
            {
                if (_blocked is not null && !_blocked.Task.IsCompleted)
                {
                    _blocked.TrySetResult();
                    release = _release!.Task;
                }
            }
            if (release is not null) await release.WaitAsync(cancellationToken);
        }
    }
}
