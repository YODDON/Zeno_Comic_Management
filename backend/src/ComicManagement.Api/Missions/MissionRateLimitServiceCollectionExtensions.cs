using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Missions;

internal static class MissionRateLimitConstants
{
    internal const string AdminMutationPolicy = "missions-admin-mutation";
    internal const int AdminMutationPermitLimit = 20;
}

internal static class MissionRateLimitServiceCollectionExtensions
{
    internal static IServiceCollection AddMissionRateLimiting(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options => options.AddPolicy(
            MissionRateLimitConstants.AdminMutationPolicy,
            httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 }
                    adminId
                    ? $"mission-admin:{adminId}"
                    : $"mission-anonymous:{httpContext.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = MissionRateLimitConstants.AdminMutationPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })));

        return services;
    }
}
