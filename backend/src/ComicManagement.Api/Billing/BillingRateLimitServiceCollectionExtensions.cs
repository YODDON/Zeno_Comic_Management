using System.Security.Claims;
using System.Threading.RateLimiting;
using ComicManagement.Billing.Payments;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ComicManagement.Api.Billing;

internal static class BillingRateLimitConstants
{
    internal const string TopUpCreatePolicy = "billing-topup-create";

    internal const string RefundCreatePolicy = "billing-refund-create";

    internal const string RefundAdminMutationPolicy = "billing-refund-admin-mutation";

    internal const string SePayWebhookPolicy = "billing-sepay-webhook";
}

internal static class BillingRateLimitServiceCollectionExtensions
{
    internal static IServiceCollection AddBillingRateLimiting(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(
                BillingRateLimitConstants.TopUpCreatePolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    GetPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            options.AddPolicy(
                BillingRateLimitConstants.RefundCreatePolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    GetPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            options.AddPolicy(
                BillingRateLimitConstants.RefundAdminMutationPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    GetPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            options.AddPolicy(
                BillingRateLimitConstants.SePayWebhookPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    GetRemoteAddressPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = httpContext.RequestServices
                            .GetRequiredService<IOptions<SePayWebhookOptions>>()
                            .Value
                            .PermitLimitPerMinute,
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

    private static string GetRemoteAddressPartitionKey(HttpContext httpContext) =>
        $"sepay:{httpContext.Connection.RemoteIpAddress}";
}
