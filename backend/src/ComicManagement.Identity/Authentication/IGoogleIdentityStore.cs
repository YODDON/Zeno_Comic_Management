using ComicManagement.Identity.Users;
using ComicManagement.Identity.Events;

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

    void AddLoginEvent(UserLoggedIn loginEvent);

    Task<IdentityStoreSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public enum IdentityStoreSaveOutcome
{
    Succeeded = 1,
    GoogleSubjectConflict = 2,
    ConcurrencyConflict = 3,
}
