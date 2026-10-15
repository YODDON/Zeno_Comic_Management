using ComicManagement.Api.Authentication;
using ComicManagement.Api.OpenApi;
using ComicManagement.Api.Reading;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Profiles;
using ComicManagement.Persistence;
using ComicManagement.Shared.Identity;
using ComicManagement.Workflows.Reading;
using ComicManagement.Billing.Payments;
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

builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
    options.AddOperationTransformer<PublicCatalogOpenApiOperationTransformer>());
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
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddIdentityAuthentication(authenticationSettings);
builder.Services.AddReadingRateLimiting(builder.Configuration);
builder.Services.Configure<OutboxOptions>(
    builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.AddOutboxWorker();
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
