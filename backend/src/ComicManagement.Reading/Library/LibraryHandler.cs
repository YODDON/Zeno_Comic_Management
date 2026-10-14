using System.Buffers.Binary;
using ComicManagement.Shared.Time;

namespace ComicManagement.Reading.Library;

public static class LibraryAvailability
{
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";
}

public enum LibraryOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
}

public sealed record FollowState(
    Guid ComicId,
    DateTimeOffset FollowedAt,
    bool Created);

public sealed record FollowStatusState(
    Guid ComicId,
    bool Followed,
    DateTimeOffset? FollowedAt);

public sealed record ContinueReadingItem(
    Guid ComicId,
    string Availability,
    string? Slug,
    string? Title,
    string? CoverAssetId,
    string? SeriesStatus,
    Guid? ChapterId,
    string? ChapterLabel,
    int? PageOrder,
    decimal? PageOffsetRatio,
    int? AcceptedMaxProgressBasisPoints,
    DateTimeOffset LastReadAt,
    string? AccessType,
    bool CanRead);

public sealed record FollowedComicItem(
    Guid ComicId,
    string Availability,
    string? Slug,
    string? Title,
    string? CoverAssetId,
    string? SeriesStatus,
    DateTimeOffset FollowedAt);

public sealed record LibraryPage<TItem>(
    IReadOnlyList<TItem> Items,
    string? NextCursor);

public sealed record FollowCommandResult(
    LibraryOutcome Outcome,
    FollowState? State = null);

public sealed record FollowStatusQueryResult(
    LibraryOutcome Outcome,
    FollowStatusState? State = null);

public sealed record LibraryQueryResult<TItem>(
    LibraryOutcome Outcome,
    LibraryPage<TItem>? Page = null);

public sealed record LibraryCursor(DateTimeOffset Timestamp, Guid ComicId);

public interface ILibraryStore
{
    Task<FollowCommandResult> FollowAsync(
        Guid userId,
        Guid comicId,
        DateTimeOffset followedAt,
        CancellationToken cancellationToken = default);

    Task UnfollowAsync(
        Guid userId,
        Guid comicId,
        CancellationToken cancellationToken = default);

    Task<FollowStatusState> GetFollowStatusAsync(
        Guid userId,
        Guid comicId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContinueReadingItem>> ListContinueAsync(
        Guid userId,
        LibraryCursor? cursor,
        int take,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FollowedComicItem>> ListFollowsAsync(
        Guid userId,
        LibraryCursor? cursor,
        int take,
        CancellationToken cancellationToken = default);
}

public sealed class LibraryHandler(ILibraryStore store, IClock clock)
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 50;
    public const int CursorLength = 32;

    public Task<FollowCommandResult> FollowAsync(
        Guid userId,
        Guid comicId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || comicId == Guid.Empty)
        {
            return Task.FromResult(new FollowCommandResult(LibraryOutcome.ValidationFailed));
        }

        var utcNow = clock.UtcNow.ToUniversalTime();
        var postgresTimestamp = new DateTimeOffset(
            utcNow.Ticks - (utcNow.Ticks % 10),
            TimeSpan.Zero);
        return store.FollowAsync(userId, comicId, postgresTimestamp, cancellationToken);
    }

    public async Task<LibraryOutcome> UnfollowAsync(
        Guid userId,
        Guid comicId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || comicId == Guid.Empty)
        {
            return LibraryOutcome.ValidationFailed;
        }

        await store.UnfollowAsync(userId, comicId, cancellationToken);
        return LibraryOutcome.Succeeded;
    }

    public async Task<FollowStatusQueryResult> GetFollowStatusAsync(
        Guid userId,
        Guid comicId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || comicId == Guid.Empty)
        {
            return new FollowStatusQueryResult(LibraryOutcome.ValidationFailed);
        }

        var state = await store.GetFollowStatusAsync(userId, comicId, cancellationToken);
        return new FollowStatusQueryResult(LibraryOutcome.Succeeded, state);
    }

    public Task<LibraryQueryResult<ContinueReadingItem>> ListContinueAsync(
        Guid userId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken = default) => ListAsync(
            userId,
            cursor,
            limit,
            store.ListContinueAsync,
            item => new LibraryCursor(item.LastReadAt, item.ComicId),
            cancellationToken);

    public Task<LibraryQueryResult<FollowedComicItem>> ListFollowsAsync(
        Guid userId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken = default) => ListAsync(
            userId,
            cursor,
            limit,
            store.ListFollowsAsync,
            item => new LibraryCursor(item.FollowedAt, item.ComicId),
            cancellationToken);

    private static async Task<LibraryQueryResult<TItem>> ListAsync<TItem>(
        Guid userId,
        string? cursor,
        int? limit,
        Func<Guid, LibraryCursor?, int, CancellationToken, Task<IReadOnlyList<TItem>>> query,
        Func<TItem, LibraryCursor> getCursor,
        CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (userId == Guid.Empty ||
            pageSize is < 1 or > MaximumPageSize ||
            !TryDecodeCursor(cursor, out var decodedCursor))
        {
            return new LibraryQueryResult<TItem>(LibraryOutcome.ValidationFailed);
        }

        var items = await query(
            userId,
            decodedCursor,
            pageSize + 1,
            cancellationToken);
        var pageItems = items.Take(pageSize).ToArray();
        var nextCursor = items.Count > pageSize
            ? EncodeCursor(getCursor(pageItems[^1]))
            : null;
        return new LibraryQueryResult<TItem>(
            LibraryOutcome.Succeeded,
            new LibraryPage<TItem>(pageItems, nextCursor));
    }

    private static string EncodeCursor(LibraryCursor cursor)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long) + 16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, cursor.Timestamp.UtcTicks);
        cursor.ComicId.TryWriteBytes(bytes[sizeof(long)..]);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeCursor(string? cursor, out LibraryCursor? value)
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
            if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            var comicId = new Guid(bytes.AsSpan(sizeof(long), 16));
            if (comicId == Guid.Empty)
            {
                return false;
            }

            value = new LibraryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), comicId);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
