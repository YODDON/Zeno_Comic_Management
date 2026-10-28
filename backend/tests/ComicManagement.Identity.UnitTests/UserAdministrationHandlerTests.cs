using ComicManagement.Identity.Administration;
using ComicManagement.Identity.Users;
using ComicManagement.Shared.Pagination;
using Xunit;

namespace ComicManagement.Identity.UnitTests;

public sealed class UserAdministrationHandlerTests
{
    [Fact]
    public void Change_status_is_monotonic_versioned_domain_mutation()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var user = User.CreateFromGoogle(Guid.NewGuid(), "Reader", null, null, createdAt);

        Assert.True(user.ChangeStatus(UserStatuses.Disabled, createdAt.AddSeconds(1)));
        Assert.Equal(UserStatuses.Disabled, user.Status);
        Assert.Equal(2, user.Version);
        Assert.False(user.ChangeStatus(UserStatuses.Disabled, createdAt.AddSeconds(2)));
        Assert.Equal(2, user.Version);
        Assert.True(user.ChangeStatus(UserStatuses.Active, createdAt.AddSeconds(3)));
        Assert.Equal(3, user.Version);
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("")]
    public void Change_status_rejects_values_outside_closed_contract(string status)
    {
        var user = User.CreateFromGoogle(Guid.NewGuid(), "Reader", null, null, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            user.ChangeStatus(status, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Handler_rejects_invalid_filter_and_status_change_without_calling_store()
    {
        var store = new RecordingStore();
        var handler = new UserAdministrationHandler(store);

        Assert.Equal(
            UserAdministrationOutcome.ValidationFailed,
            (await handler.ListAsync(null, "1", 1, 30)).Outcome);
        Assert.Equal(
            UserAdministrationOutcome.ValidationFailed,
            (await handler.ListAsync(null, null, int.MaxValue, 100)).Outcome);
        Assert.Equal(
            UserAdministrationOutcome.ValidationFailed,
            (await handler.ChangeStatusAsync(
                Guid.NewGuid(), Guid.NewGuid(), "Suspended", 1, "trace")).Outcome);
        Assert.Equal(0, store.Calls);
    }

    private sealed class RecordingStore : IUserAdministrationStore
    {
        public int Calls { get; private set; }
        public Task<UserAdministrationResult> ListAsync(string? search, string? status, OffsetPage page, CancellationToken cancellationToken = default) { Calls++; return Success(); }
        public Task<UserAdministrationResult> GetAsync(Guid userId, CancellationToken cancellationToken = default) { Calls++; return Success(); }
        public Task<UserAdministrationResult> ChangeStatusAsync(UserStatusChangeRequest request, CancellationToken cancellationToken = default) { Calls++; return Success(); }
        private static Task<UserAdministrationResult> Success() => Task.FromResult(new UserAdministrationResult(UserAdministrationOutcome.Succeeded));
    }
}
