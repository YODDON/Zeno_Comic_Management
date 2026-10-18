namespace ComicManagement.Identity.Events;

public static class IdentityEventTypes
{
    public const string UserLoggedIn = "Identity.UserLoggedIn.v1";
}

public sealed record UserLoggedIn(
    Guid LoginId,
    Guid UserId,
    DateTimeOffset LoggedInAt);
