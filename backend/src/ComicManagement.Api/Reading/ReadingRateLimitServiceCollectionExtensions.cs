using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Reading;

internal static class ReadingRateLimitConstants
{
    internal const string ProgressPolicy = "reading-progress";

    internal const string UnlockQuoteCreatePolicy = "reading-unlock-quote-create";
}

internal static class ReadingRateLimitServiceCollectionExtensions
{
    private const int DefaultPermitLimit = 60;

    internal static IServiceCollection AddReadingRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var permitLimit = configuration.GetValue<int?>(
            "Reading:ProgressRateLimit:PermitLimit") ?? DefaultPermitLimit;
        if (permitLimit is < 1 or > 1_000)
        {
            throw new InvalidOperationException(
                "Reading:ProgressRateLimit:PermitLimit must be between 1 and 1000.");
        }

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(
                ReadingRateLimitConstants.ProgressPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    GetPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            options.AddPolicy(
                ReadingRateLimitConstants.UnlockQuoteCreatePolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    GetPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
        });

        return services;
    }

    private static string GetPartitionKey(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? $"user:{userId}"
            : $"anonymous:{httpContext.Connection.RemoteIpAddress}";
}
