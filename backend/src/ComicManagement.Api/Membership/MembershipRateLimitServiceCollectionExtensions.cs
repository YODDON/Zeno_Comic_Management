using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Membership;

internal static class MembershipRateLimitConstants
{
    internal const string VipCheckoutPolicy = "membership-vip-checkout";
    internal const string AdminPlanMutationPolicy = "membership-admin-plan-mutation";
}

internal static class MembershipRateLimitServiceCollectionExtensions
{
    internal static IServiceCollection AddMembershipRateLimiting(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(
                MembershipRateLimitConstants.VipCheckoutPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 }
                        userId
                        ? $"vip-user:{userId}"
                        : $"vip-anonymous:{httpContext.Connection.RemoteIpAddress}",
                    _ => FixedWindow(10)));
            options.AddPolicy(
                MembershipRateLimitConstants.AdminPlanMutationPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 }
                        adminId
                        ? $"vip-admin:{adminId}"
                        : $"vip-admin-anonymous:{httpContext.Connection.RemoteIpAddress}",
                    _ => FixedWindow(20)));
        });
        return services;
    }

    private static FixedWindowRateLimiterOptions FixedWindow(int permitLimit) => new()
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
