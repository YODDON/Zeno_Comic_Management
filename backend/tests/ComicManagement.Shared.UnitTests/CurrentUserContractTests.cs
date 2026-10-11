using ComicManagement.Shared.Identity;
using Xunit;

namespace ComicManagement.Shared.UnitTests;

public sealed class CurrentUserContractTests
{
    [Fact]
    public void Contract_can_represent_anonymous_request()
    {
        ICurrentUser currentUser = new StubCurrentUser(
            false,
            null,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.False(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);
        Assert.False(currentUser.IsInRole("Admin"));
    }

    [Fact]
    public void Contract_can_represent_authenticated_user_and_roles()
    {
        var userId = Guid.NewGuid();
        ICurrentUser currentUser = new StubCurrentUser(
            true,
            userId,
            new HashSet<string>(["User"], StringComparer.Ordinal));

        Assert.True(currentUser.IsAuthenticated);
        Assert.Equal(userId, currentUser.UserId);
        Assert.True(currentUser.IsInRole("User"));
        Assert.False(currentUser.IsInRole("Admin"));
    }

    private sealed class StubCurrentUser(
        bool isAuthenticated,
        Guid? userId,
        IReadOnlySet<string> roles) : ICurrentUser
    {
        public bool IsAuthenticated { get; } = isAuthenticated;

        public Guid? UserId { get; } = userId;

        public bool IsInRole(string role) => roles.Contains(role);
    }
}
