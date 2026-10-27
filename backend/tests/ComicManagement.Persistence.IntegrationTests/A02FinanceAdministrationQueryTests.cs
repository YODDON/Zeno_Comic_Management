using ComicManagement.Administration.Finance;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Identity.Users;
using ComicManagement.Persistence;
using ComicManagement.Wallet.Accounts;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class A02FinanceAdministrationQueryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("a02_finance_tests").WithUsername("postgres").WithPassword("postgres").Build();
    private ServiceProvider _services = null!;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _orderId = Guid.NewGuid();
    private readonly Guid _refundId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = new ServiceCollection().AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var user = User.CreateFromGoogle(_userId, "A02 Reader", "a02@example.test", null, now);
        var order = PaymentOrder.CreateTopUp(_orderId, _userId, $"A02-{_orderId:N}",
            100_000, 10_000, "a02-order", new string('a', 64), now, now.AddMinutes(30));
        order.FulfillTopUp(now.AddMinutes(1));
        var wallet = WalletAccount.Create(_userId, now);
        wallet.CreditPaid(10_000, now.AddMinutes(1));
        var operation = WalletOperation.Create(Guid.NewGuid(), _userId,
            WalletOperationTypes.TopUpCredit, WalletOperationSourceTypes.PaymentOrder,
            _orderId, null, now.AddMinutes(1));
        var entry = WalletEntry.Create(Guid.NewGuid(), operation, WalletCoinTypes.Paid,
            WalletBuckets.Available, 10_000, 1, now.AddMinutes(1));
        var lot = PaidCoinLot.Create(Guid.NewGuid(), _userId, _orderId, 10_000, now.AddMinutes(1));
        var refund = RefundRequest.Create(_refundId, _orderId, _userId, "A02 refund",
            100_000, 10_000, "a02-refund", new string('b', 64), "v1", now.AddMinutes(2));
        var recipient = RefundRecipient.Create(Guid.NewGuid(), _refundId, "VCB",
            [1, 2, 3], [4, 5, 6], "6789", "******6789", "A*** R***", "v1", now.AddMinutes(2));
        var sharedVerifiedAt = now.AddMinutes(3);
        var olderTie = ProviderTransaction.CreateSePay(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            new SePayNormalizedTransaction(
                "a02-provider-low", new string('c', 64), 99_000,
                ProviderTransactionDirections.Incoming, "A02", "6789", "A02-low",
                now.AddMinutes(1), "AmountMismatch"),
            sharedVerifiedAt);
        olderTie.RequireReconciliation("AmountMismatch", _orderId);
        var latestTie = ProviderTransaction.CreateSePay(
            Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"),
            new SePayNormalizedTransaction(
                "a02-provider-high", new string('d', 64), 98_000,
                ProviderTransactionDirections.Incoming, "A02", "6789", "A02-high",
                now.AddMinutes(1), "LatePayment"),
            sharedVerifiedAt);
        latestTie.RequireReconciliation("LatePayment", _orderId);
        db.AddRange(user, order, wallet, operation, entry, lot, refund, recipient, olderTie, latestTie);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Admin_finance_queries_project_payment_ledger_and_refund_without_plain_recipient()
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<FinanceAdministrationQueryHandler>();

        var payments = await handler.ListPaymentsAsync("A02 Reader", "Succeeded", "TopUp", 1, 30);
        var payment = Assert.Single(payments.Value!.Items);
        Assert.Equal(_orderId, payment.PaymentId);
        Assert.Equal(100_000, payment.AmountVnd);
        Assert.Equal("LatePayment", payment.ReconciliationReason);
        var paymentDetail = await handler.GetPaymentAsync(_orderId);
        Assert.Equal("LatePayment", paymentDetail.Value!.Payment.ReconciliationReason);
        Assert.Equal(
            Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"),
            paymentDetail.Value.ProviderTransactions[0].TransactionId);

        var ledger = await handler.ListLedgerAsync("a02@example.test", "TopUpCredit", 1, 30);
        var leg = Assert.Single(ledger.Value!.Items);
        Assert.Equal(10_000, leg.DeltaCoins);
        Assert.Equal(WalletCoinTypes.Paid, leg.CoinType);

        var refunds = await handler.ListRefundsAsync("A02-", "Requested", 1, 30);
        Assert.Equal(_refundId, Assert.Single(refunds.Value!.Items).RefundId);
        var detail = await handler.GetRefundAsync(_refundId);
        Assert.Equal(FinanceAdministrationQueryOutcome.Succeeded, detail.Outcome);
        Assert.Equal("******6789", detail.Value!.Recipient.AccountNumberMasked);
        Assert.Equal(10_000, detail.Value.OriginalPaidCoinLot!.AvailableCoins);
        Assert.DoesNotContain("123456789", detail.Value.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_filters_fail_before_query_and_missing_detail_is_not_found()
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<FinanceAdministrationQueryHandler>();
        Assert.Equal(FinanceAdministrationQueryOutcome.ValidationFailed,
            (await handler.ListPaymentsAsync(null, "Unknown", null, 1, 30)).Outcome);
        Assert.Equal(FinanceAdministrationQueryOutcome.ValidationFailed,
            (await handler.ListLedgerAsync(null, null, 0, 30)).Outcome);
        Assert.Equal(FinanceAdministrationQueryOutcome.NotFound,
            (await handler.GetRefundAsync(Guid.NewGuid())).Outcome);
    }
}
