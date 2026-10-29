using ComicManagement.Persistence;
using ComicManagement.Wallet.Ledger;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

internal static class FinancialReconciliationAssertions
{
    internal static async Task AssertWalletAndLotsAsync(
        AppDbContext context,
        Guid userId)
    {
        var wallet = await context.Wallets.AsNoTracking()
            .SingleAsync(item => item.UserId == userId);
        var lotsAvailable = await context.PaidCoinLots.AsNoTracking()
            .Where(item => item.UserId == userId)
            .SumAsync(item => item.GrantedCoins - item.SpentCoins -
                item.RefundedCoins - item.ReservedCoins);
        var lotsReserved = await context.PaidCoinLots.AsNoTracking()
            .Where(item => item.UserId == userId)
            .SumAsync(item => item.ReservedCoins);
        var paidAvailableLedger = await SumLedgerAsync(
            context,
            userId,
            WalletCoinTypes.Paid,
            WalletBuckets.Available);
        var paidReservedLedger = await SumLedgerAsync(
            context,
            userId,
            WalletCoinTypes.Paid,
            WalletBuckets.Reserved);
        var bonusAvailableLedger = await SumLedgerAsync(
            context,
            userId,
            WalletCoinTypes.Bonus,
            WalletBuckets.Available);

        Assert.Equal(wallet.PaidAvailable, lotsAvailable);
        Assert.Equal(wallet.PaidReserved, lotsReserved);
        Assert.Equal(wallet.PaidAvailable, paidAvailableLedger);
        Assert.Equal(wallet.PaidReserved, paidReservedLedger);
        Assert.Equal(wallet.BonusAvailable, bonusAvailableLedger);
        Assert.True(wallet.PaidAvailable >= 0);
        Assert.True(wallet.PaidReserved >= 0);
        Assert.True(wallet.BonusAvailable >= 0);
    }

    private static Task<long> SumLedgerAsync(
        AppDbContext context,
        Guid userId,
        string coinType,
        string bucket) =>
        context.WalletEntries.AsNoTracking()
            .Where(item => item.UserId == userId &&
                item.CoinType == coinType &&
                item.Bucket == bucket)
            .SumAsync(item => item.DeltaCoins);
}
