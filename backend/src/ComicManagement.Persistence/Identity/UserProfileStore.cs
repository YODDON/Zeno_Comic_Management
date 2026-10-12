using ComicManagement.Identity.Profiles;
using ComicManagement.Identity.Users;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class UserProfileStore(AppDbContext dbContext) : IUserProfileStore
{
    public Task<User?> FindByIdAsync(
        Guid userId,
        CancellationToken cancellationToken) => dbContext.Users
        .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<UserProfileSaveOutcome> SaveChangesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return UserProfileSaveOutcome.Succeeded;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return UserProfileSaveOutcome.ConcurrencyConflict;
        }
    }
}
