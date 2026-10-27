using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Interaction;

internal static class InteractionRateLimitConstants
{
    internal const string CommentMutationPolicy = "interaction-comment-mutation";
    internal const int CommentMutationPermitLimit = 20;
}

internal static class InteractionRateLimitServiceCollectionExtensions
{
    internal static IServiceCollection AddInteractionRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(
            InteractionRateLimitConstants.CommentMutationPolicy,
            context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
                    ? $"comment:{id}"
                    : $"comment-anonymous:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = InteractionRateLimitConstants.CommentMutationPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })));
        return services;
    }
}
