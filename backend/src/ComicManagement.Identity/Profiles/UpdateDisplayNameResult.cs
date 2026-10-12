using ComicManagement.Identity.Authentication;

namespace ComicManagement.Identity.Profiles;

public sealed record UpdateDisplayNameResult(
    UpdateDisplayNameOutcome Outcome,
    AuthenticatedUser? User,
    bool SessionRefreshRequired)
{
    public static UpdateDisplayNameResult Succeeded(
        AuthenticatedUser user,
        bool sessionRefreshRequired)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UpdateDisplayNameResult(
            UpdateDisplayNameOutcome.Succeeded,
            user,
            sessionRefreshRequired);
    }

    public static UpdateDisplayNameResult ValidationFailed() =>
        new(UpdateDisplayNameOutcome.ValidationFailed, null, false);

    public static UpdateDisplayNameResult UserNotFound() =>
        new(UpdateDisplayNameOutcome.UserNotFound, null, false);

    public static UpdateDisplayNameResult ConcurrencyConflict() =>
        new(UpdateDisplayNameOutcome.ConcurrencyConflict, null, false);
}

public enum UpdateDisplayNameOutcome
{
    Succeeded = 1,
    ValidationFailed = 2,
    UserNotFound = 3,
    ConcurrencyConflict = 4,
}
