using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Membership;

internal static class MembershipRateLimitConstants
{
    internal const string VipCheckoutPolicy = "membership-vip-checkout";
}

internal static class MembershipRateLimitServiceCollectionExtensions
{
    internal static IServiceCollection AddMembershipRateLimiting(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRateLimiter(options => options.AddPolicy(
            MembershipRateLimitConstants.VipCheckoutPolicy,
            httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 }
                    userId
                    ? $"vip-user:{userId}"
                    : $"vip-anonymous:{httpContext.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })));
        return services;
    }
}
