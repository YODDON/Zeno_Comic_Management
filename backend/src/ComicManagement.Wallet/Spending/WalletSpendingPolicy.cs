using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Wallet.Spending;

public enum SpendCoinsOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    WalletUnavailable = 2,
    InsufficientCoins = 3,
    IdempotencyConflict = 4,
    InvariantViolation = 5,
}

public sealed record SpendCoinsCommand(
    Guid UserId,
    long AmountCoins,
    string OperationType,
    string SourceType,
    Guid SourceId,
    string IdempotencyKey);

public sealed record PaidCoinAllocationResult(
    Guid PaidCoinLotId,
    long AmountCoins);

public sealed record SpendCoinsReceipt(
    Guid OperationId,
    long BonusCoins,
    long PaidCoins,
    IReadOnlyList<PaidCoinAllocationResult> PaidAllocations,
    bool IsReplay);

public sealed record SpendCoinsResult(
    SpendCoinsOutcome Outcome,
    SpendCoinsReceipt? Receipt = null);

public sealed record WalletSpendDecision(
    SpendCoinsResult Result,
    WalletOperation? Operation,
    IReadOnlyList<WalletEntry> Entries,
    IReadOnlyList<WalletAllocation> Allocations);

/// <summary>
/// Applies Wallet-owned spending rules to aggregates that the caller has already locked.
/// Persistence and cross-domain transaction boundaries remain outside this policy.
/// </summary>
public static class WalletSpendingPolicy
{
    public static WalletSpendDecision Apply(
        SpendCoinsCommand command,
        WalletAccount wallet,
        IReadOnlyCollection<PaidCoinLot> paidLots,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(paidLots);

        if (!IsValid(command, occurredAt) || wallet.UserId != command.UserId)
        {
            return Failed(SpendCoinsOutcome.ValidationFailed);
        }

        var orderedLots = paidLots
            .OrderBy(lot => lot.CreatedAt)
            .ThenBy(lot => lot.Id)
            .ToArray();
        if (orderedLots.Any(lot => lot.UserId != command.UserId))
        {
            return Failed(SpendCoinsOutcome.InvariantViolation);
        }

        decimal paidAvailableFromLots = 0;
        decimal paidReservedFromLots = 0;
        foreach (var lot in orderedLots)
        {
            paidAvailableFromLots += lot.AvailableCoins;
            paidReservedFromLots += lot.ReservedCoins;
        }

        if (paidAvailableFromLots != wallet.PaidAvailable ||
            paidReservedFromLots != wallet.PaidReserved)
        {
            return Failed(SpendCoinsOutcome.InvariantViolation);
        }

        var bonusCoins = Math.Min(wallet.BonusAvailable, command.AmountCoins);
        var paidCoins = checked(command.AmountCoins - bonusCoins);
        if (wallet.PaidAvailable < paidCoins)
        {
            return Failed(SpendCoinsOutcome.InsufficientCoins);
        }

        var operation = WalletOperation.Create(
            Guid.NewGuid(),
            command.UserId,
            command.OperationType,
            command.SourceType,
            command.SourceId,
            command.IdempotencyKey,
            occurredAt);
        var entries = new List<WalletEntry>(capacity: 2);
        var allocations = new List<WalletAllocation>();
        var allocationResults = new List<PaidCoinAllocationResult>();
        var legOrder = 1;

        if (bonusCoins > 0)
        {
            wallet.SpendBonus(bonusCoins, occurredAt);
            entries.Add(WalletEntry.Create(
                Guid.NewGuid(),
                operation,
                WalletCoinTypes.Bonus,
                WalletBuckets.Available,
                -bonusCoins,
                legOrder++,
                occurredAt));
        }

        if (paidCoins > 0)
        {
            wallet.SpendPaid(paidCoins, occurredAt);
            entries.Add(WalletEntry.Create(
                Guid.NewGuid(),
                operation,
                WalletCoinTypes.Paid,
                WalletBuckets.Available,
                -paidCoins,
                legOrder,
                occurredAt));

            var remaining = paidCoins;
            foreach (var lot in orderedLots)
            {
                if (remaining == 0)
                {
                    break;
                }

                var allocated = Math.Min(lot.AvailableCoins, remaining);
                if (allocated == 0)
                {
                    continue;
                }

                lot.Spend(allocated);
                allocations.Add(WalletAllocation.Create(
                    Guid.NewGuid(),
                    operation,
                    lot,
                    allocated,
                    occurredAt));
                allocationResults.Add(new PaidCoinAllocationResult(lot.Id, allocated));
                remaining -= allocated;
            }

            if (remaining != 0)
            {
                throw new InvalidOperationException(
                    "Paid Coin lots did not satisfy the reconciled Wallet balance.");
            }
        }

        return new WalletSpendDecision(
            new SpendCoinsResult(
                SpendCoinsOutcome.Succeeded,
                new SpendCoinsReceipt(
                    operation.Id,
                    bonusCoins,
                    paidCoins,
                    allocationResults,
                    IsReplay: false)),
            operation,
            entries,
            allocations);
    }

    public static bool IsValid(SpendCoinsCommand command, DateTimeOffset occurredAt) =>
        command.UserId != Guid.Empty &&
        command.SourceId != Guid.Empty &&
        command.AmountCoins > 0 &&
        occurredAt.Offset == TimeSpan.Zero &&
        IsValidText(command.OperationType, WalletOperation.MaximumOperationTypeLength) &&
        IsValidText(command.SourceType, WalletOperation.MaximumSourceTypeLength) &&
        IsValidText(command.IdempotencyKey, WalletOperation.MaximumIdempotencyKeyLength);

    private static WalletSpendDecision Failed(SpendCoinsOutcome outcome) => new(
        new SpendCoinsResult(outcome),
        null,
        Array.Empty<WalletEntry>(),
        Array.Empty<WalletAllocation>());

    private static bool IsValidText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.Length <= maximumLength;
}
