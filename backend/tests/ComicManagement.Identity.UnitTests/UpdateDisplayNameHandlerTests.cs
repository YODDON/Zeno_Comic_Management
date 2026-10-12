using ComicManagement.Identity.Profiles;
using ComicManagement.Identity.Users;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Identity.UnitTests;

public sealed class UpdateDisplayNameHandlerTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 10, 11, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt =
        new(2026, 10, 12, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_name_is_trimmed_and_saved_with_a_new_version()
    {
        var user = CreateUser();
        var store = new StubProfileStore(user);
        var handler = new UpdateDisplayNameHandler(store, new StubClock(UpdatedAt));

        var result = await handler.HandleAsync(
            user.Id,
            "  Tên truyện mới  ",
            CancellationToken.None);

        Assert.Equal(UpdateDisplayNameOutcome.Succeeded, result.Outcome);
        Assert.Equal("Tên truyện mới", result.User!.DisplayName);
        Assert.Equal(2, result.User.Version);
        Assert.True(result.SessionRefreshRequired);
        Assert.Equal(UpdatedAt, user.UpdatedAt);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public async Task One_hundred_twenty_unicode_scalars_are_accepted()
    {
        var user = CreateUser();
        var store = new StubProfileStore(user);
        var handler = new UpdateDisplayNameHandler(store, new StubClock(UpdatedAt));
        var displayName = string.Concat(Enumerable.Repeat("\U0001F600", 120));

        var result = await handler.HandleAsync(
            user.Id,
            displayName,
            CancellationToken.None);

        Assert.Equal(UpdateDisplayNameOutcome.Succeeded, result.Outcome);
        Assert.Equal(120, result.User!.DisplayName.EnumerateRunes().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_name_is_rejected_without_loading_or_saving(string? displayName)
    {
        var store = new StubProfileStore(CreateUser());
        var handler = new UpdateDisplayNameHandler(store, new StubClock(UpdatedAt));

        var result = await handler.HandleAsync(
            Guid.NewGuid(),
            displayName,
            CancellationToken.None);

        Assert.Equal(UpdateDisplayNameOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.FindAttempts);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public async Task More_than_one_hundred_twenty_unicode_scalars_are_rejected()
    {
        var store = new StubProfileStore(CreateUser());
        var handler = new UpdateDisplayNameHandler(store, new StubClock(UpdatedAt));
        var displayName = string.Concat(Enumerable.Repeat("\U0001F600", 121));

        var result = await handler.HandleAsync(
            Guid.NewGuid(),
            displayName,
            CancellationToken.None);

        Assert.Equal(UpdateDisplayNameOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public async Task Unchanged_name_is_a_successful_no_op()
    {
        var user = CreateUser();
        var store = new StubProfileStore(user);
        var handler = new UpdateDisplayNameHandler(store, new StubClock(UpdatedAt));

        var result = await handler.HandleAsync(
            user.Id,
            $"  {user.DisplayName}  ",
            CancellationToken.None);

        Assert.Equal(UpdateDisplayNameOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.User!.Version);
        Assert.False(result.SessionRefreshRequired);
        Assert.Equal(CreatedAt, user.UpdatedAt);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public async Task Store_concurrency_conflict_is_returned_as_a_typed_result()
    {
        var user = CreateUser();
        var store = new StubProfileStore(user)
        {
            SaveOutcome = UserProfileSaveOutcome.ConcurrencyConflict,
        };
        var handler = new UpdateDisplayNameHandler(store, new StubClock(UpdatedAt));

        var result = await handler.HandleAsync(
            user.Id,
            "Concurrent name",
            CancellationToken.None);

        Assert.Equal(UpdateDisplayNameOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Null(result.User);
    }

    private static User CreateUser() => User.CreateFromGoogle(
        Guid.NewGuid(),
        "Tên ban đầu",
        "reader@example.com",
        "https://example.com/avatar.png",
        CreatedAt);

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class StubProfileStore(User? user) : IUserProfileStore
    {
        internal int FindAttempts { get; private set; }

        internal int SaveAttempts { get; private set; }

        internal UserProfileSaveOutcome SaveOutcome { get; init; } =
            UserProfileSaveOutcome.Succeeded;

        public Task<User?> FindByIdAsync(
            Guid userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindAttempts++;
            return Task.FromResult(user?.Id == userId ? user : null);
        }

        public Task<UserProfileSaveOutcome> SaveChangesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveAttempts++;
            return Task.FromResult(SaveOutcome);
        }
    }
}
