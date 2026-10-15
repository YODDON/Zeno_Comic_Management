using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;

namespace ComicManagement.Wallet.Spending;

public sealed class WalletAllocation
{
    private WalletAllocation()
    {
    }

    public Guid Id { get; private set; }

    public Guid OperationId { get; private set; }

    public Guid PaidCoinLotId { get; private set; }

    public long AmountCoins { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static WalletAllocation Create(
        Guid id,
        WalletOperation operation,
        PaidCoinLot paidCoinLot,
        long amountCoins,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Wallet allocation ID is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(paidCoinLot);
        if (operation.UserId != paidCoinLot.UserId)
        {
            throw new ArgumentException(
                "Wallet operation and Paid Coin lot must belong to the same user.",
                nameof(paidCoinLot));
        }

        if (amountCoins <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amountCoins),
                "Allocated Coin amount must be positive.");
        }

        if (createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(createdAt));
        }

        return new WalletAllocation
        {
            Id = id,
            OperationId = operation.Id,
            PaidCoinLotId = paidCoinLot.Id,
            AmountCoins = amountCoins,
            CreatedAt = createdAt,
        };
    }
}
