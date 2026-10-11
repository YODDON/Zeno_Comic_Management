namespace ComicManagement.Api.Authentication;

internal static class AuthenticationConstants
{
    internal const string SessionScheme = "ComicManagement.Session";
    internal const string GoogleScheme = "Google";
    internal const string LoginRateLimitPolicy = "authentication-login";
    internal const string CallbackPath = "/api/v1/auth/google/callback";
    internal const string DisabledAccountItem = "ComicManagement.DisabledAccount";
}

internal static class SessionClaimTypes
{
    internal const string AvatarUrl = "comic_management:avatar_url";
    internal const string Status = "comic_management:status";
    internal const string Version = "comic_management:version";
}
