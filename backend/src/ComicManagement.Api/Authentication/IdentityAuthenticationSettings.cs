namespace ComicManagement.Api.Authentication;

internal sealed record IdentityAuthenticationSettings(
    string GoogleClientId,
    string GoogleClientSecret,
    TimeSpan SessionLifetime,
    string DataProtectionKeysPath,
    string DataProtectionCertificatePath,
    string DataProtectionCertificatePassword,
    Uri ErrorDocumentationBaseUri,
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
        var certificatePath = configuration["Authentication:DataProtectionCertificatePath"];
        var certificatePassword = configuration["Authentication:DataProtectionCertificatePassword"];
        var errorDocumentationBaseUri = configuration["Api:ErrorDocumentationBaseUri"];

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

        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            throw new InvalidOperationException(
                "Authentication:DataProtectionCertificatePath is required.");
        }

        if (string.IsNullOrWhiteSpace(certificatePassword))
        {
            throw new InvalidOperationException(
                "Authentication:DataProtectionCertificatePassword is required.");
        }

        if (!Uri.TryCreate(errorDocumentationBaseUri, UriKind.Absolute, out var errorBaseUri) ||
            errorBaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(errorBaseUri.Query) ||
            !string.IsNullOrEmpty(errorBaseUri.Fragment))
        {
            throw new InvalidOperationException(
                "Api:ErrorDocumentationBaseUri must be an absolute HTTPS URI without a query or fragment.");
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
        var absoluteCertificatePath = Path.IsPathFullyQualified(certificatePath)
            ? Path.GetFullPath(certificatePath)
            : Path.GetFullPath(certificatePath, contentRootPath);

        if (!File.Exists(absoluteCertificatePath))
        {
            throw new InvalidOperationException(
                "Authentication:DataProtectionCertificatePath must identify an existing PKCS#12 certificate.");
        }

        var normalizedErrorBaseUri = new Uri(
            errorBaseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? errorBaseUri.AbsoluteUri
                : $"{errorBaseUri.AbsoluteUri}/",
            UriKind.Absolute);

        return new IdentityAuthenticationSettings(
            clientId,
            clientSecret,
            sessionLifetime,
            absoluteKeysPath,
            absoluteCertificatePath,
            certificatePassword,
            normalizedErrorBaseUri,
            permitLimit,
            rateLimitWindow);
    }
}
