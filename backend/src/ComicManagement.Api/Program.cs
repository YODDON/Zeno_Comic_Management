using ComicManagement.Api.Authentication;
using ComicManagement.Api.Billing;
using ComicManagement.Api.OpenApi;
using ComicManagement.Api.Reading;
using ComicManagement.Api.Membership;
using ComicManagement.Api.Missions;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Profiles;
using ComicManagement.Persistence;
using ComicManagement.Shared.Identity;
using ComicManagement.Workflows.Reading;
using ComicManagement.Workflows.Billing;
using ComicManagement.Billing.Payments;
using ComicManagement.Membership.Memberships;
using ComicManagement.Billing.Refunds;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

var databaseConnectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Connection string 'Database' is required.");
var authenticationSettings = IdentityAuthenticationSettings.FromConfiguration(
    builder.Configuration,
    builder.Environment.ContentRootPath);
var refundSecuritySettings = RefundSecuritySettings.FromConfiguration(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
{
    options.AddOperationTransformer<PublicCatalogOpenApiOperationTransformer>();
    options.AddOperationTransformer<SePayWebhookOpenApiOperationTransformer>();
    options.AddSchemaTransformer<TopUpAmountOpenApiSchemaTransformer>();
    options.AddSchemaTransformer<RefundOpenApiSchemaTransformer>();
});
builder.Services.AddProblemDetails();
builder.Services.AddPersistence(databaseConnectionString);
builder.Services.AddOptions<TopUpOrderOptions>()
    .Bind(builder.Configuration.GetSection(TopUpOrderOptions.SectionName))
    .Validate(
        options => options.TopUpOrderTtlMinutes > 0,
        "Billing:TopUpOrderTtlMinutes must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<TopUpOrderOptions>>().Value;
    return new TopUpOrderPolicy(TimeSpan.FromMinutes(options.TopUpOrderTtlMinutes));
});
builder.Services.AddOptions<VipOrderOptions>()
    .Bind(builder.Configuration.GetSection(VipOrderOptions.SectionName))
    .Validate(
        options => options.VipOrderTtlMinutes > 0,
        "Membership:VipOrderTtlMinutes must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<VipOrderOptions>>().Value;
    return new VipOrderPolicy(TimeSpan.FromMinutes(options.VipOrderTtlMinutes));
});
builder.Services.AddOptions<SePayWebhookOptions>()
    .Bind(builder.Configuration.GetSection(SePayWebhookOptions.SectionName))
    .Validate(
        options => options.TimestampToleranceSeconds is >= 1 and <= 900,
        "Billing:SePayWebhook:TimestampToleranceSeconds must be between 1 and 900.")
    .Validate(
        options => options.MaximumBodyBytes is >= 1024 and <= 1024 * 1024,
        "Billing:SePayWebhook:MaximumBodyBytes must be between 1024 and 1048576.")
    .Validate(
        options => options.PermitLimitPerMinute is >= 1 and <= 10_000,
        "Billing:SePayWebhook:PermitLimitPerMinute must be between 1 and 10000.")
    .Validate(
        options => !options.Enabled ||
                   options.Secret is { Length: >= 32 and <= 500 },
        "Billing:SePayWebhook:Secret must contain between 32 and 500 characters when enabled.")
    .Validate(
        options => !options.Enabled ||
                   !string.IsNullOrWhiteSpace(options.ExpectedAccountNumber) &&
                   options.ExpectedAccountNumber.Length <=
                       SePayNormalizedTransaction.MaximumAccountNumberLength,
        "Billing:SePayWebhook:ExpectedAccountNumber is required when enabled.")
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SePayWebhookOptions>>().Value;
    return new SePayWebhookPolicy(
        options.Enabled,
        options.Secret,
        options.ExpectedAccountNumber,
        TimeSpan.FromSeconds(options.TimestampToleranceSeconds),
        options.MaximumBodyBytes);
});
builder.Services.AddSingleton<SePayWebhookAdapter>();
builder.Services.AddCloudinaryChapterPageAssets(options =>
{
    var section = builder.Configuration.GetSection(CloudinaryOptions.SectionName);
    options.CloudName = section[nameof(CloudinaryOptions.CloudName)] ?? string.Empty;
    options.ApiKey = section[nameof(CloudinaryOptions.ApiKey)] ?? string.Empty;
    options.ApiSecret = section[nameof(CloudinaryOptions.ApiSecret)] ?? string.Empty;
    options.ChapterPagePublicIdPrefix =
        section[nameof(CloudinaryOptions.ChapterPagePublicIdPrefix)] ??
        options.ChapterPagePublicIdPrefix;
    options.MaximumChapterPageBytes = long.TryParse(
        section[nameof(CloudinaryOptions.MaximumChapterPageBytes)],
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var maximumChapterPageBytes)
        ? maximumChapterPageBytes
        : 0;
});
builder.Services.AddScoped<GoogleSignInHandler>();
builder.Services.AddScoped<LogoutHandler>();
builder.Services.AddScoped<UpdateDisplayNameHandler>();
builder.Services.AddScoped<PublicChapterListWorkflow>();
builder.Services.AddScoped<TopUpFulfillmentWorkflow>();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddIdentityAuthentication(authenticationSettings);
builder.Services.AddSingleton(refundSecuritySettings);
builder.Services.AddSingleton<IRefundRecipientProtector, RefundRecipientProtector>();
builder.Services.AddSingleton<IRefundRequestFingerprintService, RefundRequestFingerprintService>();
builder.Services.AddReadingRateLimiting(builder.Configuration);
builder.Services.AddBillingRateLimiting();
builder.Services.AddMembershipRateLimiting();
builder.Services.AddMissionRateLimiting();
builder.Services.Configure<OutboxOptions>(
    builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.AddOutboxWorker();
builder.Services.Configure<MissionReceiptOptions>(
    builder.Configuration.GetSection(MissionReceiptOptions.SectionName));
builder.Services.AddMissionReceiptWorker();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    foreach (var proxyEntry in builder.Configuration
                 .GetSection("ReverseProxy:KnownProxies")
                 .GetChildren())
    {
        if (!IPAddress.TryParse(proxyEntry.Value, out var proxy))
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies:{proxyEntry.Key} must be an IP address.");
        }

        options.KnownProxies.Add(proxy);
    }

    foreach (var networkEntry in builder.Configuration
                 .GetSection("ReverseProxy:KnownNetworks")
                 .GetChildren())
    {
        if (!System.Net.IPNetwork.TryParse(networkEntry.Value, out var network))
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownNetworks:{networkEntry.Key} must be an IP network in CIDR notation.");
        }

        options.KnownIPNetworks.Add(network);
    }
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapOpenApi();
app.MapControllers();

app.Run();

public partial class Program;
