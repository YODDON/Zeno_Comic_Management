using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Spending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence;

internal sealed record WalletSpendLockResult(
    SpendCoinsOutcome Outcome,
    WalletAccount? Wallet,
    IReadOnlyList<PaidCoinLot> PaidLots);

// This transaction-neutral engine is shared by W02 and later cross-module
// workflows. The caller owns the transaction and SaveChanges boundary.
internal sealed class WalletSpendEngine(
    AppDbContext dbContext,
    ILogger<WalletSpendEngine> logger)
{
    internal async Task<SpendCoinsResult> ApplyAsync(
        SpendCoinsCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Wallet spending requires an active database transaction.");
        }

        if (command is null || !WalletSpendingPolicy.IsValid(command, occurredAt))
        {
            return new SpendCoinsResult(SpendCoinsOutcome.ValidationFailed);
        }

        var locked = await AcquireAsync(command.UserId, occurredAt, cancellationToken);
        if (locked.Outcome != SpendCoinsOutcome.Succeeded)
        {
            return new SpendCoinsResult(locked.Outcome);
        }

        return await ApplyLockedAsync(command, locked, occurredAt, cancellationToken);
    }

    internal async Task<WalletSpendLockResult> AcquireAsync(
        Guid userId,
        DateTimeOffset walletCreatedAt,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Wallet spending requires an active database transaction.");
        }

        if (userId == Guid.Empty || walletCreatedAt.Offset != TimeSpan.Zero)
        {
            return new WalletSpendLockResult(
                SpendCoinsOutcome.ValidationFailed,
                null,
                Array.Empty<PaidCoinLot>());
        }

        await EnsureWalletExistsAsync(userId, walletCreatedAt, cancellationToken);
        var wallet = await LockWalletAsync(userId, cancellationToken);
        if (wallet is null)
        {
            return new WalletSpendLockResult(
                SpendCoinsOutcome.WalletUnavailable,
                null,
                Array.Empty<PaidCoinLot>());
        }

        var paidLots = await LockPaidLotsAsync(userId, cancellationToken);
        if (!await IsReconciledAsync(wallet, paidLots, cancellationToken))
        {
            logger.LogCritical(
                "Wallet reconciliation failed before spending for user {UserId}.",
                userId);
            return new WalletSpendLockResult(
                SpendCoinsOutcome.InvariantViolation,
                wallet,
                paidLots);
        }

        return new WalletSpendLockResult(SpendCoinsOutcome.Succeeded, wallet, paidLots);
    }

    internal async Task<SpendCoinsResult> ApplyLockedAsync(
        SpendCoinsCommand command,
        WalletSpendLockResult locked,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (locked.Outcome != SpendCoinsOutcome.Succeeded ||
            locked.Wallet is null ||
            locked.Wallet.UserId != command.UserId ||
            !WalletSpendingPolicy.IsValid(command, occurredAt))
        {
            return new SpendCoinsResult(SpendCoinsOutcome.ValidationFailed);
        }

        var replay = await TryReplayAsync(command, cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var decision = WalletSpendingPolicy.Apply(
            command,
            locked.Wallet,
            locked.PaidLots,
            occurredAt);
        if (decision.Result.Outcome != SpendCoinsOutcome.Succeeded)
        {
            return decision.Result;
        }

        dbContext.WalletOperations.Add(decision.Operation!);
        dbContext.WalletEntries.AddRange(decision.Entries);
        dbContext.WalletAllocations.AddRange(decision.Allocations);
        return decision.Result;
    }

    private async Task<bool> IsReconciledAsync(
        WalletAccount wallet,
        IReadOnlyCollection<ComicManagement.Wallet.Lots.PaidCoinLot> paidLots,
        CancellationToken cancellationToken)
    {
        decimal lotAvailable = 0;
        decimal lotReserved = 0;
        foreach (var lot in paidLots)
        {
            lotAvailable += lot.AvailableCoins;
            lotReserved += lot.ReservedCoins;
        }

        var paidAvailableLedger = await SumLedgerAsync(
            wallet.UserId,
            WalletCoinTypes.Paid,
            WalletBuckets.Available,
            cancellationToken);
        var paidReservedLedger = await SumLedgerAsync(
            wallet.UserId,
            WalletCoinTypes.Paid,
            WalletBuckets.Reserved,
            cancellationToken);
        var bonusAvailableLedger = await SumLedgerAsync(
            wallet.UserId,
            WalletCoinTypes.Bonus,
            WalletBuckets.Available,
            cancellationToken);
        var invalidBonusReservedEntries = await dbContext.WalletEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.UserId == wallet.UserId &&
                entry.CoinType == WalletCoinTypes.Bonus &&
                entry.Bucket == WalletBuckets.Reserved,
                cancellationToken);

        return !invalidBonusReservedEntries &&
            lotAvailable == wallet.PaidAvailable &&
            lotReserved == wallet.PaidReserved &&
            paidAvailableLedger == wallet.PaidAvailable &&
            paidReservedLedger == wallet.PaidReserved &&
            bonusAvailableLedger == wallet.BonusAvailable;
    }

    private async Task<decimal> SumLedgerAsync(
        Guid userId,
        string coinType,
        string bucket,
        CancellationToken cancellationToken) =>
        await dbContext.WalletEntries
            .AsNoTracking()
            .Where(entry =>
                entry.UserId == userId &&
                entry.CoinType == coinType &&
                entry.Bucket == bucket)
            .SumAsync(entry => (decimal)entry.DeltaCoins, cancellationToken);

    private async Task EnsureWalletExistsAsync(
        Guid userId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wallet.wallets
                (user_id, paid_available, paid_reserved, bonus_available, version, created_at, updated_at)
            SELECT id, 0, 0, 0, 1, {occurredAt}, {occurredAt}
            FROM identity.users
            WHERE id = {userId}
            ON CONFLICT (user_id) DO NOTHING
            """, cancellationToken);
    }

    private Task<WalletAccount?> LockWalletAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        dbContext.Wallets
            .FromSqlInterpolated($"""
                SELECT *
                FROM wallet.wallets
                WHERE user_id = {userId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

    private Task<List<ComicManagement.Wallet.Lots.PaidCoinLot>> LockPaidLotsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        dbContext.PaidCoinLots
            .FromSqlInterpolated($"""
                SELECT *
                FROM wallet.paid_coin_lots
                WHERE user_id = {userId}
                ORDER BY created_at, id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

    private async Task<SpendCoinsResult?> TryReplayAsync(
        SpendCoinsCommand command,
        CancellationToken cancellationToken)
    {
        var bySource = await dbContext.WalletOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(operation =>
                operation.OperationType == command.OperationType &&
                operation.SourceType == command.SourceType &&
                operation.SourceId == command.SourceId,
                cancellationToken);
        var byIdempotency = await dbContext.WalletOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(operation =>
                operation.UserId == command.UserId &&
                operation.OperationType == command.OperationType &&
                operation.IdempotencyKey == command.IdempotencyKey,
                cancellationToken);

        if (bySource is null && byIdempotency is null)
        {
            return null;
        }

        if (bySource is null ||
            byIdempotency is null ||
            bySource.Id != byIdempotency.Id ||
            bySource.UserId != command.UserId ||
            bySource.SourceType != command.SourceType ||
            bySource.SourceId != command.SourceId ||
            bySource.IdempotencyKey != command.IdempotencyKey)
        {
            return new SpendCoinsResult(SpendCoinsOutcome.IdempotencyConflict);
        }

        var entries = await dbContext.WalletEntries
            .AsNoTracking()
            .Where(entry => entry.OperationId == bySource.Id)
            .OrderBy(entry => entry.LegOrder)
            .ToListAsync(cancellationToken);
        var allocations = await (
                from allocation in dbContext.WalletAllocations.AsNoTracking()
                join lot in dbContext.PaidCoinLots.AsNoTracking()
                    on allocation.PaidCoinLotId equals lot.Id
                where allocation.OperationId == bySource.Id
                orderby lot.CreatedAt, lot.Id
                select new PaidCoinAllocationResult(
                    allocation.PaidCoinLotId,
                    allocation.AmountCoins))
            .ToListAsync(cancellationToken);

        long bonusCoins = 0;
        long paidCoins = 0;
        foreach (var entry in entries)
        {
            if (entry.Bucket != WalletBuckets.Available || entry.DeltaCoins >= 0)
            {
                return new SpendCoinsResult(SpendCoinsOutcome.InvariantViolation);
            }

            var debited = checked(-entry.DeltaCoins);
            if (entry.CoinType == WalletCoinTypes.Bonus)
            {
                bonusCoins = checked(bonusCoins + debited);
            }
            else if (entry.CoinType == WalletCoinTypes.Paid)
            {
                paidCoins = checked(paidCoins + debited);
            }
            else
            {
                return new SpendCoinsResult(SpendCoinsOutcome.InvariantViolation);
            }
        }

        long allocatedPaid = 0;
        foreach (var allocation in allocations)
        {
            allocatedPaid = checked(allocatedPaid + allocation.AmountCoins);
        }

        if (checked(bonusCoins + paidCoins) != command.AmountCoins ||
            allocatedPaid != paidCoins)
        {
            return new SpendCoinsResult(SpendCoinsOutcome.IdempotencyConflict);
        }

        return new SpendCoinsResult(
            SpendCoinsOutcome.Succeeded,
            new SpendCoinsReceipt(
                bySource.Id,
                bonusCoins,
                paidCoins,
                allocations,
                IsReplay: true));
    }
}
