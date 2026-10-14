using ComicManagement.Wallet.Queries;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class WalletQueryStore(AppDbContext dbContext) : IWalletQueryStore
{
    public async Task<WalletBalanceState> GetOrCreateBalanceAsync(
        Guid userId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wallet.wallets
                (user_id, paid_available, paid_reserved, bonus_available, version, created_at, updated_at)
            VALUES
                ({userId}, 0, 0, 0, 1, {createdAt}, {createdAt})
            ON CONFLICT (user_id) DO NOTHING
            """, cancellationToken);

        return await dbContext.Wallets
            .AsNoTracking()
            .Where(wallet => wallet.UserId == userId)
            .Select(wallet => new WalletBalanceState(
                wallet.PaidAvailable,
                wallet.PaidReserved,
                wallet.BonusAvailable,
                wallet.PaidAvailable + wallet.BonusAvailable,
                wallet.Version))
            .SingleAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WalletEntryState>> ListEntriesAsync(
        Guid userId,
        WalletCursor? cursor,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.WalletEntries
            .AsNoTracking()
            .Where(entry => entry.UserId == userId);

        if (cursor is not null)
        {
            query = query.Where(entry =>
                entry.CreatedAt < cursor.OccurredAt ||
                (entry.CreatedAt == cursor.OccurredAt && entry.Id.CompareTo(cursor.EntryId) < 0));
        }

        return await query
            .Join(
                dbContext.WalletOperations.AsNoTracking(),
                entry => new { entry.OperationId, entry.UserId },
                operation => new { OperationId = operation.Id, operation.UserId },
                (entry, operation) => new { Entry = entry, Operation = operation })
            .OrderByDescending(row => row.Entry.CreatedAt)
            .ThenByDescending(row => row.Entry.Id)
            .Take(take)
            .Select(row => new WalletEntryState(
                row.Entry.Id,
                row.Entry.OperationId,
                row.Operation.OperationType,
                row.Operation.SourceType,
                row.Operation.SourceId,
                row.Entry.CoinType,
                row.Entry.Bucket,
                row.Entry.DeltaCoins,
                row.Entry.CreatedAt))
            .ToListAsync(cancellationToken);
    }
}
