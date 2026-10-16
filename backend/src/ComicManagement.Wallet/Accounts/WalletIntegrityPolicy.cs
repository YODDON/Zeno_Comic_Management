namespace ComicManagement.Wallet.Accounts;

public readonly record struct WalletIntegritySnapshot(
    decimal PaidAvailable,
    decimal PaidReserved,
    decimal BonusAvailable,
    decimal PaidLotAvailable,
    decimal PaidLotReserved,
    decimal LedgerPaidAvailable,
    decimal LedgerPaidReserved,
    decimal LedgerBonusAvailable);

public static class WalletIntegrityPolicy
{
    public static bool IsReconciled(WalletIntegritySnapshot snapshot) =>
        snapshot.PaidAvailable >= 0 &&
        snapshot.PaidReserved >= 0 &&
        snapshot.BonusAvailable >= 0 &&
        snapshot.PaidAvailable == snapshot.PaidLotAvailable &&
        snapshot.PaidReserved == snapshot.PaidLotReserved &&
        snapshot.PaidAvailable == snapshot.LedgerPaidAvailable &&
        snapshot.PaidReserved == snapshot.LedgerPaidReserved &&
        snapshot.BonusAvailable == snapshot.LedgerBonusAvailable;
}
