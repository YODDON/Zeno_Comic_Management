using ComicManagement.Api.Authentication;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Profiles;
using ComicManagement.Persistence;
using ComicManagement.Shared.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

var databaseConnectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Connection string 'Database' is required.");
var authenticationSettings = IdentityAuthenticationSettings.FromConfiguration(
    builder.Configuration,
    builder.Environment.ContentRootPath);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddPersistence(databaseConnectionString);
builder.Services.AddScoped<GoogleSignInHandler>();
builder.Services.AddScoped<LogoutHandler>();
builder.Services.AddScoped<UpdateDisplayNameHandler>();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddIdentityAuthentication(authenticationSettings);
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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapControllers();

app.Run();

public partial class Program;
