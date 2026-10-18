using System.Text;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Users;
using ComicManagement.Identity.Events;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Identity.UnitTests;

public sealed class GoogleSignInHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 12, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_sign_in_creates_active_user_role_bound_to_exact_subject()
    {
        var store = new StubIdentityStore();
        var handler = new GoogleSignInHandler(store, new StubClock(Now));

        var result = await handler.HandleAsync(
            new GoogleUserProfile(
                "google-subject-01",
                "  Nguyen Van A  ",
                " user@example.com ",
                "https://lh3.googleusercontent.com/avatar"),
            CancellationToken.None);

        Assert.Equal(GoogleSignInOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.User);
        Assert.Equal("Nguyen Van A", result.User.DisplayName);
        Assert.Equal("user@example.com", result.User.Email);
        Assert.Equal(IdentityRoles.User, result.User.Role);
        Assert.Equal(UserStatuses.Active, result.User.Status);
        Assert.Equal("google-subject-01", store.GoogleSubject);
        Assert.Equal(1, store.User!.Version);
        var loginEvent = Assert.Single(store.LoginEvents);
        Assert.Equal(store.User.Id, loginEvent.UserId);
        Assert.Equal(Now, loginEvent.LoggedInAt);
        Assert.NotEqual(Guid.Empty, loginEvent.LoginId);
    }

    [Fact]
    public async Task Returning_sign_in_updates_provider_attributes_without_overwriting_display_name()
    {
        var existing = User.CreateFromGoogle(
            Guid.NewGuid(),
            "Ten da chinh sua",
            "old@example.com",
            "https://example.com/old.png",
            Now.AddDays(-1));
        var store = new StubIdentityStore(existing, "stable-subject");
        var handler = new GoogleSignInHandler(store, new StubClock(Now));

        var result = await handler.HandleAsync(
            new GoogleUserProfile(
                "stable-subject",
                "Google Name",
                "new@example.com",
                "https://example.com/new.png"),
            CancellationToken.None);

        Assert.Equal(GoogleSignInOutcome.Succeeded, result.Outcome);
        Assert.Equal("Ten da chinh sua", result.User!.DisplayName);
        Assert.Equal("new@example.com", result.User.Email);
        Assert.Equal("https://example.com/new.png", result.User.AvatarUrl);
        Assert.Equal(2, result.User.Version);
        Assert.Equal(existing.Id, Assert.Single(store.LoginEvents).UserId);
    }

    [Fact]
    public async Task Login_event_time_is_canonicalized_to_postgresql_precision()
    {
        var sourceTime = Now.AddTicks(7);
        var store = new StubIdentityStore();
        var handler = new GoogleSignInHandler(store, new StubClock(sourceTime));

        await handler.HandleAsync(
            new GoogleUserProfile("precision-subject", "Precision", null, null),
            CancellationToken.None);

        Assert.Equal(Now, Assert.Single(store.LoginEvents).LoggedInAt);
        Assert.Equal(Now, store.User!.CreatedAt);
    }

    [Theory]
    [InlineData("", "Google User", "https://example.com/avatar.png")]
    [InlineData(" subject-with-space ", "Google User", "https://example.com/avatar.png")]
    [InlineData("subject", "", "https://example.com/avatar.png")]
    [InlineData("subject", "Google User", "http://example.com/avatar.png")]
    public async Task Invalid_provider_profile_is_rejected(
        string subject,
        string displayName,
        string avatarUrl)
    {
        var handler = new GoogleSignInHandler(
            new StubIdentityStore(),
            new StubClock(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(
            new GoogleUserProfile(subject, displayName, null, avatarUrl),
            CancellationToken.None));
    }

    [Fact]
    public async Task Subject_conflict_reloads_the_winning_identity()
    {
        var winningUser = User.CreateFromGoogle(
            Guid.NewGuid(),
            "Winner",
            "winner@example.com",
            null,
            Now);
        var store = new StubIdentityStore
        {
            ConflictWinner = winningUser,
            NextSaveOutcome = IdentityStoreSaveOutcome.GoogleSubjectConflict,
        };
        var handler = new GoogleSignInHandler(store, new StubClock(Now));

        var result = await handler.HandleAsync(
            new GoogleUserProfile(
                "same-subject",
                "Concurrent User",
                "winner@example.com",
                null),
            CancellationToken.None);

        Assert.Equal(winningUser.Id, result.User!.Id);
        Assert.Equal(2, store.SaveAttempts);
    }

    [Fact]
    public async Task Repeated_concurrency_conflicts_return_a_typed_failure()
    {
        var store = new StubIdentityStore
        {
            AlwaysConflict = true,
        };
        var handler = new GoogleSignInHandler(store, new StubClock(Now));

        await Assert.ThrowsAsync<IdentityConcurrencyException>(() => handler.HandleAsync(
            new GoogleUserProfile(
                "conflicting-subject",
                "Concurrent User",
                "concurrent@example.com",
                null),
            CancellationToken.None));

        Assert.Equal(3, store.SaveAttempts);
    }

    [Fact]
    public async Task Display_name_is_limited_by_unicode_scalar_values()
    {
        var store = new StubIdentityStore();
        var handler = new GoogleSignInHandler(store, new StubClock(Now));
        var displayName = string.Concat(Enumerable.Repeat("😀", 130));

        var result = await handler.HandleAsync(
            new GoogleUserProfile("unicode-subject", displayName, null, null),
            CancellationToken.None);

        Assert.Equal(120, result.User!.DisplayName.EnumerateRunes().Count());
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class StubIdentityStore : IGoogleIdentityStore
    {
        internal StubIdentityStore()
        {
        }

        internal StubIdentityStore(User user, string googleSubject)
        {
            User = user;
            GoogleSubject = googleSubject;
        }

        internal User? User { get; private set; }

        internal string? GoogleSubject { get; private set; }

        internal User? ConflictWinner { get; init; }

        internal IdentityStoreSaveOutcome NextSaveOutcome { get; set; } =
            IdentityStoreSaveOutcome.Succeeded;

        internal bool AlwaysConflict { get; init; }

        internal int SaveAttempts { get; private set; }

        internal List<UserLoggedIn> LoginEvents { get; } = [];

        private UserLoggedIn? PendingLoginEvent { get; set; }

        public Task<User?> FindByGoogleSubjectAsync(
            string googleSubject,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(string.Equals(
                GoogleSubject,
                googleSubject,
                StringComparison.Ordinal)
                ? User
                : null);
        }

        public Task<User?> FindUserByIdAsync(
            Guid userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(User?.Id == userId ? User : null);
        }

        public void Add(User user, GoogleIdentity googleIdentity)
        {
            User = user;
            GoogleSubject = googleIdentity.GoogleSubject;
        }

        public void AddLoginEvent(UserLoggedIn loginEvent) =>
            PendingLoginEvent = loginEvent;

        public Task<IdentityStoreSaveOutcome> SaveChangesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveAttempts++;

            if (AlwaysConflict)
            {
                User = null;
                GoogleSubject = null;
                PendingLoginEvent = null;
                return Task.FromResult(IdentityStoreSaveOutcome.ConcurrencyConflict);
            }

            var outcome = NextSaveOutcome;
            NextSaveOutcome = IdentityStoreSaveOutcome.Succeeded;

            if (outcome == IdentityStoreSaveOutcome.GoogleSubjectConflict &&
                ConflictWinner is not null)
            {
                User = ConflictWinner;
            }

            if (outcome == IdentityStoreSaveOutcome.Succeeded && PendingLoginEvent is not null)
            {
                LoginEvents.Add(PendingLoginEvent);
            }

            PendingLoginEvent = null;

            return Task.FromResult(outcome);
        }
    }
}
