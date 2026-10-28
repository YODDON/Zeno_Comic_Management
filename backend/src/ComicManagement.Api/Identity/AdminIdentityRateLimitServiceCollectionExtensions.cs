using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Identity;

internal static class AdminIdentityRateLimitConstants
{
    internal const string MutationPolicy = "identity-admin-mutation";
    internal const int MutationPermitLimit = 20;
}

internal static class AdminIdentityRateLimitServiceCollectionExtensions
{
    internal static IServiceCollection AddAdminIdentityRateLimiting(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRateLimiter(options => options.AddPolicy(
            AdminIdentityRateLimitConstants.MutationPolicy,
            context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } adminId
                    ? $"identity-admin:{adminId}"
                    : $"identity-anonymous:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = AdminIdentityRateLimitConstants.MutationPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })));
        return services;
    }
}
