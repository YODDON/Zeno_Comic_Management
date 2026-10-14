using System.Buffers.Binary;
using ComicManagement.Shared.Time;

namespace ComicManagement.Wallet.Queries;

public enum WalletQueryOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
}

public sealed record WalletBalanceState(
    long PaidAvailable,
    long PaidReserved,
    long BonusAvailable,
    long TotalAvailable,
    long Version);

public sealed record WalletEntryState(
    Guid Id,
    Guid OperationId,
    string OperationType,
    string SourceType,
    Guid SourceId,
    string CoinType,
    string Bucket,
    long DeltaCoins,
    DateTimeOffset OccurredAt);

public sealed record WalletCursor(DateTimeOffset OccurredAt, Guid EntryId);

public sealed record WalletEntryPage(
    IReadOnlyList<WalletEntryState> Items,
    string? NextCursor);

public sealed record WalletBalanceQueryResult(
    WalletQueryOutcome Outcome,
    WalletBalanceState? Balance = null);

public sealed record WalletEntriesQueryResult(
    WalletQueryOutcome Outcome,
    WalletEntryPage? Page = null);

public interface IWalletQueryStore
{
    Task<WalletBalanceState> GetOrCreateBalanceAsync(
        Guid userId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WalletEntryState>> ListEntriesAsync(
        Guid userId,
        WalletCursor? cursor,
        int take,
        CancellationToken cancellationToken = default);
}

public sealed class WalletQueryHandler(IWalletQueryStore store, IClock clock)
{
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 100;
    public const int CursorLength = 32;

    public async Task<WalletBalanceQueryResult> GetBalanceAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return new WalletBalanceQueryResult(WalletQueryOutcome.ValidationFailed);
        }

        var balance = await store.GetOrCreateBalanceAsync(
            userId,
            NormalizePostgreSqlTimestamp(clock.UtcNow),
            cancellationToken);
        return new WalletBalanceQueryResult(WalletQueryOutcome.Succeeded, balance);
    }

    public async Task<WalletEntriesQueryResult> ListEntriesAsync(
        Guid userId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (userId == Guid.Empty ||
            pageSize is < 1 or > MaximumPageSize ||
            !TryDecodeCursor(cursor, out var decodedCursor))
        {
            return new WalletEntriesQueryResult(WalletQueryOutcome.ValidationFailed);
        }

        var entries = await store.ListEntriesAsync(
            userId,
            decodedCursor,
            pageSize + 1,
            cancellationToken);
        var pageItems = entries.Take(pageSize).ToArray();
        var nextCursor = entries.Count > pageSize
            ? EncodeCursor(pageItems[^1])
            : null;
        return new WalletEntriesQueryResult(
            WalletQueryOutcome.Succeeded,
            new WalletEntryPage(pageItems, nextCursor));
    }

    private static DateTimeOffset NormalizePostgreSqlTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    private static string EncodeCursor(WalletEntryState entry)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long) + 16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, entry.OccurredAt.UtcTicks);
        entry.Id.TryWriteBytes(bytes[sizeof(long)..]);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeCursor(string? cursor, out WalletCursor? value)
    {
        value = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length != CursorLength)
        {
            return false;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - normalized.Length % 4) % 4),
                '=');
            var bytes = Convert.FromBase64String(normalized);
            if (bytes.Length != sizeof(long) + 16)
            {
                return false;
            }

            var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
            if (ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            var entryId = new Guid(bytes.AsSpan(sizeof(long), 16));
            if (entryId == Guid.Empty)
            {
                return false;
            }

            value = new WalletCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                entryId);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
