using ComicManagement.Shared.Time;

namespace ComicManagement.Identity.Authentication;

public sealed class LogoutHandler(
    ISessionStore sessionStore,
    IClock clock)
{
    public Task HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        return sessionStore.RevokeAllAsync(userId, clock.UtcNow, cancellationToken);
    }
}
