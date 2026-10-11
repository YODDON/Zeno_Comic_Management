using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ComicManagement.Api.Authentication;

internal static class IdentityAuthenticationServiceCollectionExtensions
{
    internal static IServiceCollection AddIdentityAuthentication(
        this IServiceCollection services,
        IdentityAuthenticationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        services.AddHttpContextAccessor();
        services.AddScoped<GoogleOpenIdConnectEvents>();
        services.AddScoped<SessionCookieEvents>();

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(settings.DataProtectionKeysPath))
            .SetApplicationName("ComicManagement");

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = AuthenticationConstants.SessionScheme;
                options.DefaultSignInScheme = AuthenticationConstants.SessionScheme;
                options.DefaultChallengeScheme = AuthenticationConstants.GoogleScheme;
            })
            .AddCookie(AuthenticationConstants.SessionScheme, options =>
            {
                options.Cookie.Name = "__Host-ComicManagement.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = settings.SessionLifetime;
                options.SlidingExpiration = false;
                options.EventsType = typeof(SessionCookieEvents);
            })
            .AddOpenIdConnect(AuthenticationConstants.GoogleScheme, options =>
            {
                options.SignInScheme = AuthenticationConstants.SessionScheme;
                options.Authority = "https://accounts.google.com";
                options.ClientId = settings.GoogleClientId;
                options.ClientSecret = settings.GoogleClientSecret;
                options.CallbackPath = AuthenticationConstants.CallbackPath;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.ValidIssuers =
                [
                    "https://accounts.google.com",
                    "accounts.google.com",
                ];
                options.TokenValidationParameters.ValidAudience = settings.GoogleClientId;
                options.TokenValidationParameters.NameClaimType = "name";
                options.CorrelationCookie.HttpOnly = true;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.CorrelationCookie.SameSite = SameSiteMode.None;
                options.CorrelationCookie.IsEssential = true;
                options.NonceCookie.HttpOnly = true;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.NonceCookie.SameSite = SameSiteMode.None;
                options.NonceCookie.IsEssential = true;
                options.EventsType = typeof(GoogleOpenIdConnectEvents);
            });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(
                        MetadataName.RetryAfter,
                        out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                }

                return new ValueTask(ApiProblemWriter.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    "RATE_LIMITED",
                    "Too many requests",
                    "Try signing in again later."));
            };

            options.AddPolicy(
                AuthenticationConstants.LoginRateLimitPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.LoginPermitLimit,
                        Window = settings.LoginRateLimitWindow,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
        });

        return services;
    }
}
