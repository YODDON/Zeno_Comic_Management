using ComicManagement.Identity.Users;

namespace ComicManagement.Identity.Profiles;

public interface IUserProfileStore
{
    Task<User?> FindByIdAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<UserProfileSaveOutcome> SaveChangesAsync(
        CancellationToken cancellationToken);
}

public enum UserProfileSaveOutcome
{
    Succeeded = 1,
    ConcurrencyConflict = 2,
}
