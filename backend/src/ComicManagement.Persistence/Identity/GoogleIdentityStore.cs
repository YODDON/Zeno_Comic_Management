using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ComicManagement.Persistence;

internal sealed class GoogleIdentityStore(AppDbContext dbContext) : IGoogleIdentityStore
{
    private const string GoogleSubjectConstraint = "uq_google_identities_google_subject";

    public Task<User?> FindByGoogleSubjectAsync(
        string googleSubject,
        CancellationToken cancellationToken) => dbContext.GoogleIdentities
        .Where(identity => identity.GoogleSubject == googleSubject)
        .Select(identity => identity.User)
        .SingleOrDefaultAsync(cancellationToken);

    public Task<User?> FindUserByIdAsync(
        Guid userId,
        CancellationToken cancellationToken) => dbContext.Users
        .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public void Add(User user, GoogleIdentity googleIdentity)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(googleIdentity);

        dbContext.Users.Add(user);
        dbContext.GoogleIdentities.Add(googleIdentity);
    }

    public async Task<IdentityStoreSaveOutcome> SaveChangesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return IdentityStoreSaveOutcome.Succeeded;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return IdentityStoreSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: GoogleSubjectConstraint,
            })
        {
            dbContext.ChangeTracker.Clear();
            return IdentityStoreSaveOutcome.GoogleSubjectConflict;
        }
    }
}
