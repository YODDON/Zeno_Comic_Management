using ComicManagement.Shared.Time;
using ComicManagement.Wallet.Queries;
using Xunit;

namespace ComicManagement.Wallet.UnitTests;

public sealed class WalletQueryHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Balance_is_lazily_created_with_server_time()
    {
        var store = new FakeWalletQueryStore();
        var userId = Guid.NewGuid();
        var handler = new WalletQueryHandler(store, new StubClock(Now.AddTicks(9)));

        var result = await handler.GetBalanceAsync(userId);

        Assert.Equal(WalletQueryOutcome.Succeeded, result.Outcome);
        Assert.Equal(userId, store.CreatedForUserId);
        Assert.Equal(Now, store.CreatedAt);
        Assert.Equal(0, result.Balance!.TotalAvailable);
    }

    [Fact]
    public async Task Entries_use_opaque_cursor_and_do_not_accept_invalid_bounds()
    {
        var entries = new[]
        {
            Entry(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Now),
            Entry(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Now),
            Entry(Guid.Parse("11111111-1111-1111-1111-111111111111"), Now.AddSeconds(-1)),
        };
        var store = new FakeWalletQueryStore(entries);
        var handler = new WalletQueryHandler(store, new StubClock(Now));

        var first = await handler.ListEntriesAsync(Guid.NewGuid(), null, 2);

        Assert.Equal(WalletQueryOutcome.Succeeded, first.Outcome);
        Assert.Equal(2, first.Page!.Items.Count);
        Assert.Equal(WalletQueryHandler.CursorLength, first.Page.NextCursor!.Length);
        Assert.Equal(3, store.LastTake);

        await handler.ListEntriesAsync(Guid.NewGuid(), first.Page.NextCursor, 2);
        Assert.Equal(entries[1].Id, store.LastCursor!.EntryId);
        Assert.Equal(entries[1].OccurredAt, store.LastCursor.OccurredAt);

        Assert.Equal(
            WalletQueryOutcome.ValidationFailed,
            (await handler.ListEntriesAsync(Guid.NewGuid(), "not-a-cursor", 30)).Outcome);
        Assert.Equal(
            WalletQueryOutcome.ValidationFailed,
            (await handler.ListEntriesAsync(Guid.NewGuid(), null, 101)).Outcome);
        Assert.Equal(
            WalletQueryOutcome.ValidationFailed,
            (await handler.ListEntriesAsync(Guid.Empty, null, 30)).Outcome);
    }

    private static WalletEntryState Entry(Guid id, DateTimeOffset occurredAt) => new(
        id,
        Guid.NewGuid(),
        "TopUpCredit",
        "PaymentOrder",
        Guid.NewGuid(),
        "Paid",
        "Available",
        10,
        occurredAt);

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class FakeWalletQueryStore(
        IReadOnlyList<WalletEntryState>? entries = null) : IWalletQueryStore
    {
        public Guid CreatedForUserId { get; private set; }

        public DateTimeOffset CreatedAt { get; private set; }

        public WalletCursor? LastCursor { get; private set; }

        public int LastTake { get; private set; }

        public Task<WalletBalanceState> GetOrCreateBalanceAsync(
            Guid userId,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken = default)
        {
            CreatedForUserId = userId;
            CreatedAt = createdAt;
            return Task.FromResult(new WalletBalanceState(0, 0, 0, 0, 1));
        }

        public Task<IReadOnlyList<WalletEntryState>> ListEntriesAsync(
            Guid userId,
            WalletCursor? cursor,
            int take,
            CancellationToken cancellationToken = default)
        {
            LastCursor = cursor;
            LastTake = take;
            return Task.FromResult(entries ?? (IReadOnlyList<WalletEntryState>)[]);
        }
    }
}
