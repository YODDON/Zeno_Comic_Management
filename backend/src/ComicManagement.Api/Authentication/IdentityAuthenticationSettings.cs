namespace ComicManagement.Api.Authentication;

internal sealed record IdentityAuthenticationSettings(
    string GoogleClientId,
    string GoogleClientSecret,
    TimeSpan SessionLifetime,
    string DataProtectionKeysPath,
    int LoginPermitLimit,
    TimeSpan LoginRateLimitWindow)
{
    internal static IdentityAuthenticationSettings FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        var clientId = configuration["Authentication:Google:ClientId"];
        var clientSecret = configuration["Authentication:Google:ClientSecret"];
        var keysPath = configuration["Authentication:DataProtectionKeysPath"];

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "Authentication:Google:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Authentication:Google:ClientSecret is required.");
        }

        if (!TimeSpan.TryParse(
                configuration["Authentication:SessionLifetime"],
                out var sessionLifetime) ||
            sessionLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Authentication:SessionLifetime must be positive.");
        }

        if (string.IsNullOrWhiteSpace(keysPath))
        {
            throw new InvalidOperationException(
                "Authentication:DataProtectionKeysPath is required.");
        }

        if (!int.TryParse(
                configuration["Authentication:LoginRateLimit:PermitLimit"],
                out var permitLimit) ||
            permitLimit is < 1 or > 1000)
        {
            throw new InvalidOperationException(
                "Authentication:LoginRateLimit:PermitLimit must be between 1 and 1000.");
        }

        if (!TimeSpan.TryParse(
                configuration["Authentication:LoginRateLimit:Window"],
                out var rateLimitWindow) ||
            rateLimitWindow < TimeSpan.FromSeconds(1) ||
            rateLimitWindow > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                "Authentication:LoginRateLimit:Window must be between one second and one hour.");
        }

        var absoluteKeysPath = Path.IsPathFullyQualified(keysPath)
            ? Path.GetFullPath(keysPath)
            : Path.GetFullPath(keysPath, contentRootPath);

        return new IdentityAuthenticationSettings(
            clientId,
            clientSecret,
            sessionLifetime,
            absoluteKeysPath,
            permitLimit,
            rateLimitWindow);
    }
}
