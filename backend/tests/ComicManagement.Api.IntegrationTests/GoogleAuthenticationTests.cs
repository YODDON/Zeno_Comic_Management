using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComicManagement.Persistence;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Api.IntegrationTests;

public sealed class GoogleAuthenticationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_api_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private AuthenticationApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new AuthenticationApplicationFactory(_postgres.GetConnectionString());

        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .Database
            .MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Unauthenticated_me_returns_problem_details_without_redirect()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTH_REQUIRED", problem.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Google_callback_creates_secure_session_and_me_profile()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-session",
            "Nguyen Van B",
            "user-b@example.com",
            "https://example.com/b.png");
        using var client = CreateClient();

        var flow = await BeginLoginAsync(client, "/library");
        var callback = await CompleteLoginAsync(client, flow);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/library", callback.Headers.Location?.OriginalString);

        var sessionCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(
                "__Host-ComicManagement.Session=",
                StringComparison.Ordinal));
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);

        var meResponse = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nguyen Van B", me.GetProperty("displayName").GetString());
        Assert.Equal("user-b@example.com", me.GetProperty("email").GetString());
        Assert.Equal("User", me.GetProperty("role").GetString());
        Assert.Equal("Active", me.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Replayed_callback_is_rejected_and_does_not_duplicate_identity()
    {
        const string subject = "google-subject-replay";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "Replay User",
            "replay@example.com",
            null);
        using var client = CreateClient();
        var flow = await BeginLoginAsync(client, "/");

        var first = await CompleteLoginAsync(client, flow);
        var replay = await client.GetAsync(flow.CallbackUrl);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            1,
            await context.GoogleIdentities.CountAsync(
                identity => identity.GoogleSubject == subject));
    }

    [Fact]
    public async Task Repeated_login_for_same_subject_reuses_user()
    {
        const string subject = "google-subject-repeat";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "Original Name",
            "original@example.com",
            null);

        using var firstClient = CreateClient();
        await CompleteLoginAsync(firstClient, await BeginLoginAsync(firstClient, "/"));

        _factory.TokenEndpoint.SetProfile(
            subject,
            "Provider Name Changed",
            "changed@example.com",
            "https://example.com/changed.png");
        using var secondClient = CreateClient();
        await CompleteLoginAsync(secondClient, await BeginLoginAsync(secondClient, "/"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identity = await context.GoogleIdentities
            .Where(candidate => candidate.GoogleSubject == subject)
            .Select(candidate => new
            {
                candidate.User.Id,
                candidate.User.DisplayName,
                candidate.User.Email,
                candidate.User.AvatarUrl,
            })
            .SingleAsync();

        Assert.Equal("Original Name", identity.DisplayName);
        Assert.Equal("changed@example.com", identity.Email);
        Assert.Equal("https://example.com/changed.png", identity.AvatarUrl);
    }

    [Fact]
    public async Task Disabled_account_loses_existing_session()
    {
        const string subject = "google-subject-disabled";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "Disabled User",
            "disabled@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/"));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Users
                .Where(user => context.GoogleIdentities.Any(identity =>
                    identity.UserId == user.Id && identity.GoogleSubject == subject))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(user => user.Status, "Disabled")
                    .SetProperty(user => user.Version, user => user.Version + 1));
        }

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FORBIDDEN", problem.GetProperty("errorCode").GetString());
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                "__Host-ComicManagement.Session=;",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_account_cannot_create_a_new_session()
    {
        const string subject = "google-subject-disabled-login";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "Disabled Login User",
            "disabled-login@example.com",
            null);
        using (var firstClient = CreateClient())
        {
            await CompleteLoginAsync(
                firstClient,
                await BeginLoginAsync(firstClient, "/"));
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Users
                .Where(user => context.GoogleIdentities.Any(identity =>
                    identity.UserId == user.Id && identity.GoogleSubject == subject))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(user => user.Status, "Disabled")
                    .SetProperty(user => user.Version, user => user.Version + 1));
        }

        using var secondClient = CreateClient();
        var callback = await CompleteLoginAsync(
            secondClient,
            await BeginLoginAsync(secondClient, "/"));

        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        var problem = await callback.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FORBIDDEN", problem.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(
            callback.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                "__Host-ComicManagement.Session=",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task External_return_url_is_replaced_with_root()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-redirect",
            "Redirect User",
            "redirect@example.com",
            null);
        using var client = CreateClient();

        var flow = await BeginLoginAsync(client, "https://evil.example/steal");
        var callback = await CompleteLoginAsync(client, flow);

        Assert.Equal("/", callback.Headers.Location?.OriginalString);
    }

    private HttpClient CreateClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

    private async Task<LoginFlow> BeginLoginAsync(HttpClient client, string returnUrl)
    {
        var response = await client.GetAsync(
            $"/api/v1/auth/google/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var authorizationUri = response.Headers.Location!;
        var query = QueryHelpers.ParseQuery(authorizationUri.Query);
        var state = Assert.Single(query["state"])
            ?? throw new InvalidOperationException("OIDC state is missing.");
        var nonce = Assert.Single(query["nonce"])
            ?? throw new InvalidOperationException("OIDC nonce is missing.");
        _factory.TokenEndpoint.Nonce = nonce;

        var challengeCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(challengeCookies, value =>
            value.Contains("Correlation", StringComparison.Ordinal) &&
            value.Contains("secure", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("samesite=none", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(challengeCookies, value =>
            value.Contains("OpenIdConnect.Nonce", StringComparison.Ordinal) &&
            value.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.True(query.ContainsKey("code_challenge"));
        Assert.Equal("S256", Assert.Single(query["code_challenge_method"]));

        return new LoginFlow(
            $"/api/v1/auth/google/callback?code=test-code&state={Uri.EscapeDataString(state)}");
    }

    private static Task<HttpResponseMessage> CompleteLoginAsync(
        HttpClient client,
        LoginFlow flow) => client.GetAsync(flow.CallbackUrl);

    private sealed record LoginFlow(string CallbackUrl);

    private sealed class AuthenticationApplicationFactory(
        string connectionString) : WebApplicationFactory<Program>
    {
        private readonly string _keysPath = Path.Combine(
            Path.GetTempPath(),
            $"comic-management-i01-{Guid.NewGuid():N}");

        internal FakeGoogleTokenEndpoint TokenEndpoint { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:Database", connectionString)
                .UseSetting(
                    "Authentication:Google:ClientId",
                    FakeGoogleTokenEndpoint.ClientId)
                .UseSetting(
                    "Authentication:Google:ClientSecret",
                    "test-client-secret")
                .UseSetting("Authentication:SessionLifetime", "01:00:00")
                .UseSetting("Authentication:DataProtectionKeysPath", _keysPath)
                .UseSetting("Authentication:LoginRateLimit:PermitLimit", "1000")
                .UseSetting("Authentication:LoginRateLimit:Window", "00:01:00");
            builder.ConfigureServices(services =>
                services.PostConfigure<OpenIdConnectOptions>("Google", options =>
                {
                    var configuration = TokenEndpoint.CreateConfiguration();
                    options.Configuration = configuration;
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.Backchannel = new HttpClient(TokenEndpoint);
                }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TokenEndpoint.Dispose();

            if (disposing && Directory.Exists(_keysPath))
            {
                Directory.Delete(_keysPath, recursive: true);
            }
        }
    }

    private sealed class FakeGoogleTokenEndpoint : HttpMessageHandler
    {
        internal const string ClientId = "comic-management-test-client";
        private const string Issuer = "https://accounts.google.com";
        private static readonly Uri AuthorizationEndpoint =
            new("https://accounts.google.test/authorize");
        private static readonly Uri TokenEndpoint =
            new("https://accounts.google.test/token");

        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RsaSecurityKey _signingKey;
        private string _subject = "test-subject";
        private string _displayName = "Test User";
        private string? _email = "test@example.com";
        private string? _avatarUrl;

        internal FakeGoogleTokenEndpoint()
        {
            _signingKey = new RsaSecurityKey(_rsa)
            {
                KeyId = "test-google-key",
            };
        }

        internal string Nonce { get; set; } = string.Empty;

        internal void SetProfile(
            string subject,
            string displayName,
            string? email,
            string? avatarUrl)
        {
            _subject = subject;
            _displayName = displayName;
            _email = email;
            _avatarUrl = avatarUrl;
        }

        internal OpenIdConnectConfiguration CreateConfiguration()
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                AuthorizationEndpoint = AuthorizationEndpoint.AbsoluteUri,
                TokenEndpoint = TokenEndpoint.AbsoluteUri,
            };
            configuration.SigningKeys.Add(_signingKey);
            return configuration;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.RequestUri != TokenEndpoint)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var claims = new Dictionary<string, object>
            {
                ["sub"] = _subject,
                ["name"] = _displayName,
                ["email_verified"] = true,
                ["nonce"] = Nonce,
            };

            if (_email is not null)
            {
                claims["email"] = _email;
            }

            if (_avatarUrl is not null)
            {
                claims["picture"] = _avatarUrl;
            }

            var now = DateTime.UtcNow;
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = ClientId,
                Claims = claims,
                IssuedAt = now,
                NotBefore = now.AddMinutes(-1),
                Expires = now.AddMinutes(5),
                SigningCredentials = new SigningCredentials(
                    _signingKey,
                    SecurityAlgorithms.RsaSha256),
            });

            var payload = JsonSerializer.Serialize(new
            {
                access_token = "test-access-token",
                token_type = "Bearer",
                expires_in = 300,
                id_token = idToken,
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _rsa.Dispose();
            }
        }
    }
}
