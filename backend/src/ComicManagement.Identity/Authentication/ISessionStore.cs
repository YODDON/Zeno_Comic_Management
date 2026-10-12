namespace ComicManagement.Identity.Authentication;

public interface ISessionStore
{
    Task RevokeAllAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);
}
