using ComicManagement.Identity.Users;

namespace ComicManagement.Identity.Authentication;

public interface IGoogleIdentityStore
{
    Task<User?> FindByGoogleSubjectAsync(
        string googleSubject,
        CancellationToken cancellationToken);

    Task<User?> FindUserByIdAsync(
        Guid userId,
        CancellationToken cancellationToken);

    void Add(User user, GoogleIdentity googleIdentity);

    Task<IdentityStoreSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public enum IdentityStoreSaveOutcome
{
    Succeeded = 1,
    GoogleSubjectConflict = 2,
    ConcurrencyConflict = 3,
}
