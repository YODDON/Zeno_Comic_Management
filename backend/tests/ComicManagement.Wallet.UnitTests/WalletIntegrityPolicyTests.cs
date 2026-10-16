using ComicManagement.Wallet.Accounts;
using Xunit;

namespace ComicManagement.Wallet.UnitTests;

public sealed class WalletIntegrityPolicyTests
{
    [Fact]
    public void Matching_wallet_lots_and_ledger_is_reconciled()
    {
        var snapshot = new WalletIntegritySnapshot(
            PaidAvailable: 80,
            PaidReserved: 20,
            BonusAvailable: 10,
            PaidLotAvailable: 80,
            PaidLotReserved: 20,
            LedgerPaidAvailable: 80,
            LedgerPaidReserved: 20,
            LedgerBonusAvailable: 10);

        Assert.True(WalletIntegrityPolicy.IsReconciled(snapshot));
    }

    [Theory]
    [InlineData(81, 20, 10, 80, 20, 80, 20, 10)]
    [InlineData(80, 20, 10, 79, 20, 80, 20, 10)]
    [InlineData(80, 20, 10, 80, 20, 79, 20, 10)]
    [InlineData(80, 20, 10, 80, 20, 80, 20, 9)]
    public void Any_wallet_lot_or_ledger_drift_is_not_reconciled(
        long paidAvailable,
        long paidReserved,
        long bonusAvailable,
        long paidLotAvailable,
        long paidLotReserved,
        long ledgerPaidAvailable,
        long ledgerPaidReserved,
        long ledgerBonusAvailable)
    {
        var snapshot = new WalletIntegritySnapshot(
            paidAvailable,
            paidReserved,
            bonusAvailable,
            paidLotAvailable,
            paidLotReserved,
            ledgerPaidAvailable,
            ledgerPaidReserved,
            ledgerBonusAvailable);

        Assert.False(WalletIntegrityPolicy.IsReconciled(snapshot));
    }
}
