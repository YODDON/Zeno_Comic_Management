namespace ComicManagement.Identity.Authentication;

public sealed record GoogleSignInResult(
    GoogleSignInOutcome Outcome,
    AuthenticatedUser? User)
{
    public static GoogleSignInResult Succeeded(AuthenticatedUser user) =>
        new(GoogleSignInOutcome.Succeeded, user);

    public static GoogleSignInResult Disabled() =>
        new(GoogleSignInOutcome.Disabled, null);
}

public enum GoogleSignInOutcome
{
    Succeeded = 1,
    Disabled = 2,
}
