using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ComicManagement.Persistence;
using ComicManagement.Identity.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Api.IntegrationTests;

public sealed class GoogleAuthenticationTests : IAsyncLifetime
{
    private const string CertificatePassword = "i01-test-certificate-password";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_api_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private AuthenticationApplicationFactory _factory = null!;
    private readonly string _securityDirectory = Path.Combine(
        Path.GetTempPath(),
        $"comic-management-i01-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_securityDirectory);
        CreateDataProtectionCertificate(
            Path.Combine(_securityDirectory, "data-protection.pfx"),
            CertificatePassword);
        await _postgres.StartAsync();
        _factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);

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
        Directory.Delete(_securityDirectory, recursive: true);
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
        Assert.Equal(
            "https://docs.example.test/errors/AUTH_REQUIRED",
            problem.GetProperty("type").GetString());
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
        Assert.True(_factory.TokenEndpoint.LastPkceExchangeWasValid);
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

    [Fact]
    public async Task Logout_requires_csrf_and_revokes_all_copies_of_the_session()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-logout",
            "Logout User",
            "logout@example.com",
            null);
        using var client = CreateClient();
        var callback = await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/"));
        var stolenSessionCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(
                "__Host-ComicManagement.Session=",
                StringComparison.Ordinal))
            .Split(';', 2)[0];

        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        using var logoutRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/google/logout");
        logoutRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);

        var logout = await client.SendAsync(logoutRequest);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/me")).StatusCode);

        using var stolenClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        stolenClient.DefaultRequestHeaders.Add("Cookie", stolenSessionCookie);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await stolenClient.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_without_csrf_is_rejected_without_revoking_session()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-logout-csrf",
            "CSRF User",
            "csrf@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/"));

        var logout = await client.PostAsync(
            "/api/v1/auth/google/logout",
            content: null);

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        var problem = await logout.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Persisted_encrypted_key_ring_accepts_session_in_a_new_app_instance()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-restart",
            "Restart User",
            "restart@example.com",
            null);
        using var client = CreateClient();
        var callback = await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/"));
        var sessionCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(
                "__Host-ComicManagement.Session=",
                StringComparison.Ordinal))
            .Split(';', 2)[0];

        await using var restartedFactory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        using var restartedClient = restartedFactory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false,
            });
        restartedClient.DefaultRequestHeaders.Add("Cookie", sessionCookie);

        var response = await restartedClient.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var keyDocument = Directory.GetFiles(
                Path.Combine(_securityDirectory, "keys"),
                "key-*.xml")
            .Select(File.ReadAllText)
            .Single();
        Assert.Contains("encryptedSecret", keyDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", keyDocument, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TokenFault.WrongIssuer)]
    [InlineData(TokenFault.WrongAudience)]
    [InlineData(TokenFault.Expired)]
    [InlineData(TokenFault.InvalidSignature)]
    [InlineData(TokenFault.UnverifiedEmail)]
    [InlineData(TokenFault.MissingSubject)]
    public async Task Invalid_google_identity_token_is_rejected(TokenFault fault)
    {
        _factory.TokenEndpoint.SetProfile(
            $"google-subject-{fault}",
            "Invalid Token User",
            "invalid-token@example.com",
            null);
        _factory.TokenEndpoint.Fault = fault;
        using var client = CreateClient();

        var callback = await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/"));

        Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        Assert.DoesNotContain(
            callback.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                "__Host-ComicManagement.Session=",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Google_network_failure_returns_provider_unavailable()
    {
        _factory.TokenEndpoint.ProviderUnavailable = true;
        using var client = CreateClient();

        var callback = await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, callback.StatusCode);
        var problem = await callback.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PROVIDER_UNAVAILABLE", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Exhausted_identity_concurrency_returns_conflict()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            forceIdentityConflicts: true);
        factory.TokenEndpoint.SetProfile(
            "google-subject-concurrency-error",
            "Concurrency User",
            "concurrency-error@example.com",
            null);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

        var callback = await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/", factory.TokenEndpoint));

        Assert.Equal(HttpStatusCode.Conflict, callback.StatusCode);
        var problem = await callback.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONCURRENCY_CONFLICT", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Forwarded_scheme_builds_https_callback_and_rate_limit_uses_client_ip()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            loginPermitLimit: 1);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://internal-api"),
            HandleCookies = false,
        });

        var first = await SendForwardedLoginAsync(client, "198.51.100.10");
        var sameIp = await SendForwardedLoginAsync(client, "198.51.100.10");
        var anotherIp = await SendForwardedLoginAsync(client, "198.51.100.11");

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var authorizationQuery = QueryHelpers.ParseQuery(first.Headers.Location!.Query);
        Assert.StartsWith(
            "https://internal-api/",
            Assert.Single(authorizationQuery["redirect_uri"]),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameIp.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, anotherIp.StatusCode);
    }

    private HttpClient CreateClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

    private Task<LoginFlow> BeginLoginAsync(HttpClient client, string returnUrl) =>
        BeginLoginAsync(client, returnUrl, _factory.TokenEndpoint);

    private static async Task<LoginFlow> BeginLoginAsync(
        HttpClient client,
        string returnUrl,
        FakeGoogleTokenEndpoint tokenEndpoint)
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
        tokenEndpoint.Nonce = nonce;

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
        tokenEndpoint.CodeChallenge = Assert.Single(query["code_challenge"])!;

        return new LoginFlow(
            $"/api/v1/auth/google/callback?code=test-code&state={Uri.EscapeDataString(state)}");
    }

    private static Task<HttpResponseMessage> SendForwardedLoginAsync(
        HttpClient client,
        string clientIp)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/auth/google/login");
        request.Headers.Add("X-Forwarded-For", clientIp);
        request.Headers.Add("X-Forwarded-Proto", "https");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> CompleteLoginAsync(
        HttpClient client,
        LoginFlow flow) => client.GetAsync(flow.CallbackUrl);

    private sealed record LoginFlow(string CallbackUrl);

    private sealed class AuthenticationApplicationFactory(
        string connectionString,
        string securityDirectory,
        int loginPermitLimit = 1000,
        bool forceIdentityConflicts = false) : WebApplicationFactory<Program>
    {
        private readonly string _keysPath = Path.Combine(securityDirectory, "keys");
        private readonly string _certificatePath = Path.Combine(
            securityDirectory,
            "data-protection.pfx");

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
                .UseSetting(
                    "Authentication:DataProtectionCertificatePath",
                    _certificatePath)
                .UseSetting(
                    "Authentication:DataProtectionCertificatePassword",
                    CertificatePassword)
                .UseSetting(
                    "Api:ErrorDocumentationBaseUri",
                    "https://docs.example.test/errors")
                .UseSetting(
                    "Authentication:LoginRateLimit:PermitLimit",
                    loginPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .UseSetting("Authentication:LoginRateLimit:Window", "00:01:00")
                .UseSetting("ReverseProxy:KnownNetworks:0", "0.0.0.0/0")
                .UseSetting("ReverseProxy:KnownNetworks:1", "::/0");
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>("Google", options =>
                {
                    var configuration = TokenEndpoint.CreateConfiguration();
                    options.Configuration = configuration;
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.Backchannel = new HttpClient(TokenEndpoint);
                });

                if (forceIdentityConflicts)
                {
                    services.RemoveAll<IGoogleIdentityStore>();
                    services.AddScoped<IGoogleIdentityStore, AlwaysConflictIdentityStore>();
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TokenEndpoint.Dispose();
        }
    }

    private sealed class AlwaysConflictIdentityStore : IGoogleIdentityStore
    {
        public Task<ComicManagement.Identity.Users.User?> FindByGoogleSubjectAsync(
            string googleSubject,
            CancellationToken cancellationToken) => Task.FromResult<ComicManagement.Identity.Users.User?>(null);

        public Task<ComicManagement.Identity.Users.User?> FindUserByIdAsync(
            Guid userId,
            CancellationToken cancellationToken) => Task.FromResult<ComicManagement.Identity.Users.User?>(null);

        public void Add(
            ComicManagement.Identity.Users.User user,
            ComicManagement.Identity.Users.GoogleIdentity googleIdentity)
        {
        }

        public Task<IdentityStoreSaveOutcome> SaveChangesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(IdentityStoreSaveOutcome.ConcurrencyConflict);
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
        private readonly RSA _untrustedRsa = RSA.Create(2048);
        private readonly RsaSecurityKey _signingKey;
        private readonly RsaSecurityKey _untrustedSigningKey;
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
            _untrustedSigningKey = new RsaSecurityKey(_untrustedRsa)
            {
                KeyId = "untrusted-google-key",
            };
        }

        internal string Nonce { get; set; } = string.Empty;

        internal string CodeChallenge { get; set; } = string.Empty;

        internal bool LastPkceExchangeWasValid { get; private set; }

        internal TokenFault Fault { get; set; }

        internal bool ProviderUnavailable { get; set; }

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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.RequestUri != TokenEndpoint)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (ProviderUnavailable)
            {
                throw new HttpRequestException("Simulated Google outage.");
            }

            var tokenRequest = QueryHelpers.ParseQuery(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var codeVerifier = Assert.Single(tokenRequest["code_verifier"]);
            LastPkceExchangeWasValid = string.Equals(
                CodeChallenge,
                WebEncoders.Base64UrlEncode(SHA256.HashData(
                    Encoding.ASCII.GetBytes(codeVerifier!))),
                StringComparison.Ordinal);

            if (!LastPkceExchangeWasValid)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            var claims = new Dictionary<string, object>
            {
                ["name"] = _displayName,
                ["email_verified"] = Fault != TokenFault.UnverifiedEmail,
                ["nonce"] = Nonce,
            };

            if (Fault != TokenFault.MissingSubject)
            {
                claims["sub"] = _subject;
            }

            if (_email is not null)
            {
                claims["email"] = _email;
            }

            if (_avatarUrl is not null)
            {
                claims["picture"] = _avatarUrl;
            }

            var now = DateTime.UtcNow;
            var expired = Fault == TokenFault.Expired;
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Fault == TokenFault.WrongIssuer
                    ? "https://attacker.example"
                    : Issuer,
                Audience = Fault == TokenFault.WrongAudience
                    ? "another-client"
                    : ClientId,
                Claims = claims,
                IssuedAt = expired ? now.AddMinutes(-10) : now,
                NotBefore = expired ? now.AddMinutes(-10) : now.AddMinutes(-1),
                Expires = expired ? now.AddMinutes(-5) : now.AddMinutes(5),
                SigningCredentials = new SigningCredentials(
                    Fault == TokenFault.InvalidSignature
                        ? _untrustedSigningKey
                        : _signingKey,
                    SecurityAlgorithms.RsaSha256),
            });

            var payload = JsonSerializer.Serialize(new
            {
                access_token = "test-access-token",
                token_type = "Bearer",
                expires_in = 300,
                id_token = idToken,
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _rsa.Dispose();
                _untrustedRsa.Dispose();
            }
        }
    }

    public enum TokenFault
    {
        None = 0,
        WrongIssuer = 1,
        WrongAudience = 2,
        Expired = 3,
        InvalidSignature = 4,
        UnverifiedEmail = 5,
        MissingSubject = 6,
    }

    private static void CreateDataProtectionCertificate(string path, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ComicManagement I01 Tests",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
            critical: true));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(
            path,
            certificate.Export(X509ContentType.Pkcs12, password));
    }
}
