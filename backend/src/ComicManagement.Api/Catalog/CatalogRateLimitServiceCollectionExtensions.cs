using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Catalog;

internal static class CatalogRateLimitConstants
{
    internal const string AdminMutationPolicy = "catalog-admin-mutation";
    internal const int AdminMutationPermitLimit = 20;
}

internal static class CatalogRateLimitServiceCollectionExtensions
{
    internal static IServiceCollection AddCatalogRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(
            CatalogRateLimitConstants.AdminMutationPolicy,
            context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } adminId
                    ? $"catalog-admin:{adminId}"
                    : $"catalog-anonymous:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = CatalogRateLimitConstants.AdminMutationPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })));
        return services;
    }
}
