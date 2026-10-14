using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Spending;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;
using Xunit;

namespace ComicManagement.Wallet.UnitTests;

public sealed class WalletModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_wallet_starts_at_zero_and_guards_negative_transitions()
    {
        var wallet = WalletAccount.Create(Guid.NewGuid(), Now);

        Assert.Equal(0, wallet.PaidAvailable);
        Assert.Equal(0, wallet.PaidReserved);
        Assert.Equal(0, wallet.BonusAvailable);
        Assert.Equal(0, wallet.TotalAvailable);
        Assert.Equal(1, wallet.Version);
        Assert.Throws<InvalidOperationException>(() => wallet.SpendPaid(1, Now));
        Assert.Throws<InvalidOperationException>(() => wallet.SpendBonus(1, Now));
        Assert.Throws<InvalidOperationException>(() => wallet.ReservePaid(1, Now));
    }

    [Fact]
    public void Wallet_keeps_available_reserved_and_bonus_buckets_distinct()
    {
        var wallet = WalletAccount.Create(Guid.NewGuid(), Now);

        wallet.CreditPaid(100, Now.AddSeconds(1));
        wallet.CreditBonus(30, Now.AddSeconds(2));
        wallet.ReservePaid(40, Now.AddSeconds(3));
        wallet.ConsumeReservedPaid(10, Now.AddSeconds(4));
        wallet.ReleaseReservedPaid(30, Now.AddSeconds(5));
        wallet.SpendBonus(20, Now.AddSeconds(6));

        Assert.Equal(90, wallet.PaidAvailable);
        Assert.Equal(0, wallet.PaidReserved);
        Assert.Equal(10, wallet.BonusAvailable);
        Assert.Equal(100, wallet.TotalAvailable);
    }

    [Fact]
    public void Paid_lot_tracks_spent_refunded_and_reserved_as_distinct_counters()
    {
        var lot = PaidCoinLot.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            100,
            Now);

        lot.Spend(20);
        lot.Reserve(50);
        lot.RefundReserved(30);
        lot.ReleaseReserved(20);

        Assert.Equal(20, lot.SpentCoins);
        Assert.Equal(30, lot.RefundedCoins);
        Assert.Equal(0, lot.ReservedCoins);
        Assert.Equal(50, lot.AvailableCoins);
        Assert.Throws<InvalidOperationException>(() => lot.Spend(51));
    }

    [Fact]
    public void Ledger_entry_rejects_zero_delta_and_bonus_reservation()
    {
        var operation = WalletOperation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "MissionReward",
            "MissionGrant",
            Guid.NewGuid(),
            "mission-key",
            Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => WalletEntry.Create(
            Guid.NewGuid(),
            operation,
            WalletCoinTypes.Paid,
            WalletBuckets.Available,
            0,
            1,
            Now));
        Assert.Throws<ArgumentException>(() => WalletEntry.Create(
            Guid.NewGuid(),
            operation,
            WalletCoinTypes.Bonus,
            WalletBuckets.Reserved,
            1,
            1,
            Now));
    }

    [Fact]
    public void Wallet_overflow_is_rejected_without_partially_mutating_state()
    {
        var wallet = WalletAccount.Create(Guid.NewGuid(), Now);
        wallet.CreditPaid(long.MaxValue, Now);

        Assert.Throws<OverflowException>(() => wallet.CreditBonus(1, Now));
        Assert.Equal(long.MaxValue, wallet.PaidAvailable);
        Assert.Equal(0, wallet.BonusAvailable);
        Assert.Equal(2, wallet.Version);
    }

    [Fact]
    public void Allocation_requires_matching_owner_and_positive_amount()
    {
        var userId = Guid.NewGuid();
        var operation = WalletOperation.Create(
            Guid.NewGuid(),
            userId,
            "ChapterUnlock",
            "ReadingUnlock",
            Guid.NewGuid(),
            "unlock-key",
            Now);
        var ownedLot = PaidCoinLot.Create(
            Guid.NewGuid(), userId, Guid.NewGuid(), 100, Now);
        var otherLot = PaidCoinLot.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100, Now);

        var allocation = WalletAllocation.Create(
            Guid.NewGuid(), operation, ownedLot, 25, Now);

        Assert.Equal(operation.Id, allocation.OperationId);
        Assert.Equal(ownedLot.Id, allocation.PaidCoinLotId);
        Assert.Equal(25, allocation.AmountCoins);
        Assert.Throws<ArgumentException>(() => WalletAllocation.Create(
            Guid.NewGuid(), operation, otherLot, 25, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => WalletAllocation.Create(
            Guid.NewGuid(), operation, ownedLot, 0, Now));
    }
}
