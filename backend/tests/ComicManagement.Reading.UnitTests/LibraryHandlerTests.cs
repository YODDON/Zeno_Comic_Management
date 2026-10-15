using ComicManagement.Reading.Library;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Reading.UnitTests;

public sealed class LibraryHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Follow_rejects_empty_identifiers_without_calling_store()
    {
        var store = new StubStore();
        var handler = new LibraryHandler(store, new StubClock(Now));

        var result = await handler.FollowAsync(Guid.Empty, Guid.NewGuid());

        Assert.Equal(LibraryOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.FollowCalls);
    }

    [Fact]
    public async Task Follow_normalizes_timestamp_to_PostgreSQL_microsecond_precision()
    {
        var preciseNow = Now.AddTicks(7);
        var store = new StubStore();
        var handler = new LibraryHandler(store, new StubClock(preciseNow));

        await handler.FollowAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(Now, store.LastFollowedAt);
    }

    [Fact]
    public async Task Follow_status_returns_current_user_owned_state()
    {
        var comicId = Guid.NewGuid();
        var store = new StubStore
        {
            FollowStatus = new FollowStatusState(comicId, true, Now),
        };
        var handler = new LibraryHandler(store, new StubClock(Now));

        var result = await handler.GetFollowStatusAsync(Guid.NewGuid(), comicId);

        Assert.Equal(LibraryOutcome.Succeeded, result.Outcome);
        Assert.True(result.State!.Followed);
        Assert.Equal(Now, result.State.FollowedAt);
    }

    [Fact]
    public async Task Continue_uses_stable_cursor_and_bounded_page_size()
    {
        var userId = Guid.NewGuid();
        var firstComicId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var secondComicId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var store = new StubStore
        {
            ContinueItems =
            [
                ContinueItem(firstComicId, Now),
                ContinueItem(secondComicId, Now.AddMinutes(-1)),
            ],
        };
        var handler = new LibraryHandler(store, new StubClock(Now));

        var first = await handler.ListContinueAsync(userId, null, 1);
        var invalid = await handler.ListContinueAsync(userId, "invalid", 1);
        var next = await handler.ListContinueAsync(
            userId,
            first.Page!.NextCursor,
            1);

        Assert.Equal(LibraryOutcome.Succeeded, first.Outcome);
        Assert.Single(first.Page.Items);
        Assert.NotNull(first.Page.NextCursor);
        Assert.Equal(LibraryHandler.CursorLength, first.Page.NextCursor!.Length);
        Assert.Equal(LibraryOutcome.ValidationFailed, invalid.Outcome);
        Assert.Equal(firstComicId, store.LastCursor!.ComicId);
        Assert.Equal(Now, store.LastCursor.Timestamp);
        Assert.Equal(LibraryOutcome.Succeeded, next.Outcome);
    }

    private static ContinueReadingItem ContinueItem(Guid comicId, DateTimeOffset lastReadAt) => new(
        comicId,
        LibraryAvailability.Available,
        "comic",
        "Comic",
        null,
        "Ongoing",
        Guid.NewGuid(),
        "Chapter 1",
        1,
        0.5m,
        5_000,
        lastReadAt,
        "Free",
        true);

    private sealed class StubStore : ILibraryStore
    {
        internal int FollowCalls { get; private set; }

        internal DateTimeOffset? LastFollowedAt { get; private set; }

        internal LibraryCursor? LastCursor { get; private set; }

        internal IReadOnlyList<ContinueReadingItem> ContinueItems { get; init; } = [];

        internal FollowStatusState? FollowStatus { get; init; }

        public Task<FollowCommandResult> FollowAsync(
            Guid userId,
            Guid comicId,
            DateTimeOffset followedAt,
            CancellationToken cancellationToken = default)
        {
            FollowCalls++;
            LastFollowedAt = followedAt;
            return Task.FromResult(new FollowCommandResult(LibraryOutcome.NotFound));
        }

        public Task UnfollowAsync(
            Guid userId,
            Guid comicId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<FollowStatusState> GetFollowStatusAsync(
            Guid userId,
            Guid comicId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(FollowStatus ?? new FollowStatusState(comicId, false, null));

        public Task<IReadOnlyList<ContinueReadingItem>> ListContinueAsync(
            Guid userId,
            LibraryCursor? cursor,
            int take,
            CancellationToken cancellationToken = default)
        {
            LastCursor = cursor;
            return Task.FromResult(ContinueItems);
        }

        public Task<IReadOnlyList<FollowedComicItem>> ListFollowsAsync(
            Guid userId,
            LibraryCursor? cursor,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FollowedComicItem>>([]);
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
