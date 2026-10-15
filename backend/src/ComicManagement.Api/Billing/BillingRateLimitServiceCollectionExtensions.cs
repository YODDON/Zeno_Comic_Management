using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Billing;

internal static class BillingRateLimitConstants
{
    internal const string TopUpCreatePolicy = "billing-topup-create";
}

internal static class BillingRateLimitServiceCollectionExtensions
{
    internal static IServiceCollection AddBillingRateLimiting(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options => options.AddPolicy(
            BillingRateLimitConstants.TopUpCreatePolicy,
            httpContext => RateLimitPartition.GetFixedWindowLimiter(
                GetPartitionKey(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })));

        return services;
    }

    private static string GetPartitionKey(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? $"user:{userId}"
            : $"anonymous:{httpContext.Connection.RemoteIpAddress}";
}
