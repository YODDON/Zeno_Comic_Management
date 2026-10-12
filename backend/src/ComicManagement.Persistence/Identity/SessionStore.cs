using ComicManagement.Identity.Authentication;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class SessionStore(AppDbContext dbContext) : ISessionStore
{
    public async Task RevokeAllAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var affectedRows = await dbContext.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.Version, user => user.Version + 1)
                    .SetProperty(user => user.UpdatedAt, revokedAt),
                cancellationToken);

        if (affectedRows != 1)
        {
            throw new InvalidOperationException("The authenticated user no longer exists.");
        }
    }
}
