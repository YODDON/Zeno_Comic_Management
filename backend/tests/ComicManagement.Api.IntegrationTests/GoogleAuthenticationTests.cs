using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ComicManagement.Persistence;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Profiles;
using ComicManagement.Identity.Users;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
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
    public async Task Unauthenticated_admin_route_returns_unauthorized_problem_details()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/v1/i02-test/admin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTH_REQUIRED", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task User_is_forbidden_from_admin_route_and_admin_is_authorized_after_new_login()
    {
        const string subject = "google-subject-role-policy";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "Role Policy User",
            "role-policy@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/"));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.GetAsync("/api/v1/i02-test/user")).StatusCode);

        var forbidden = await client.GetAsync("/api/v1/i02-test/admin");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Null(forbidden.Headers.Location);
        var forbiddenProblem = await forbidden.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FORBIDDEN", forbiddenProblem.GetProperty("errorCode").GetString());

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var changed = await context.Users
                .Where(user => context.GoogleIdentities.Any(identity =>
                    identity.UserId == user.Id && identity.GoogleSubject == subject))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(user => user.Role, "Admin")
                    .SetProperty(user => user.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(user => user.Version, user => user.Version + 1));
            Assert.Equal(1, changed);
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/i02-test/admin")).StatusCode);

        using var adminClient = CreateClient();
        await CompleteLoginAsync(adminClient, await BeginLoginAsync(adminClient, "/"));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await adminClient.GetAsync("/api/v1/i02-test/admin")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await adminClient.GetAsync("/api/v1/i02-test/user")).StatusCode);
    }

    [Fact]
    public async Task Authenticated_mutations_require_global_csrf_validation()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-global-csrf",
            "Global CSRF User",
            "global-csrf@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/"));

        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        var unsafeMethods = new[]
        {
            HttpMethod.Post,
            HttpMethod.Put,
            HttpMethod.Patch,
            HttpMethod.Delete,
        };

        foreach (var method in unsafeMethods)
        {
            using var rejectedRequest = new HttpRequestMessage(
                method,
                "/api/v1/i02-test/mutation");
            var rejected = await client.SendAsync(rejectedRequest);

            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "VALIDATION_FAILED",
                problem.GetProperty("errorCode").GetString());

            using var acceptedRequest = new HttpRequestMessage(
                method,
                "/api/v1/i02-test/mutation");
            acceptedRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.SendAsync(acceptedRequest)).StatusCode);
        }
    }

    [Fact]
    public async Task Anonymous_mutation_is_not_subject_to_session_csrf()
    {
        using var client = CreateClient();

        var response = await client.PostAsync(
            "/api/v1/i02-test/public-mutation",
            content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Profile_display_name_update_is_persisted_without_overposting_identity_fields()
    {
        const string subject = "google-subject-profile-update";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "Tên ban đầu",
            "profile@example.com",
            "https://example.com/profile.png");
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/profile"));

        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/me")
        {
            Content = JsonContent.Create(new
            {
                displayName = "  Tên hiển thị mới 😀  ",
                email = "attacker@example.com",
                role = "Admin",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(response.Headers.GetValues("X-CSRF-TOKEN"));
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Tên hiển thị mới 😀", updated.GetProperty("displayName").GetString());
        Assert.Equal("profile@example.com", updated.GetProperty("email").GetString());
        Assert.Equal("User", updated.GetProperty("role").GetString());

        var refreshed = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var refreshedProfile = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "Tên hiển thị mới 😀",
            refreshedProfile.GetProperty("displayName").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await context.GoogleIdentities
            .Where(identity => identity.GoogleSubject == subject)
            .Select(identity => new
            {
                identity.User.DisplayName,
                identity.User.Email,
                identity.User.Role,
                identity.User.Version,
            })
            .SingleAsync();
        Assert.Equal("Tên hiển thị mới 😀", persisted.DisplayName);
        Assert.Equal("profile@example.com", persisted.Email);
        Assert.Equal("User", persisted.Role);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public async Task Unchanged_profile_name_does_not_reissue_the_session_cookie()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-profile-noop",
            "No-op Name",
            "profile-noop@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/profile"));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/me")
        {
            Content = JsonContent.Create(new { displayName = "  No-op Name  " }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(cookie => cookie.StartsWith(
                "__Host-ComicManagement.Session=",
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Profile_update_refreshes_current_cookie_and_revokes_older_cookie_copies()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-profile-session-version",
            "Session Version User",
            "profile-session@example.com",
            null);
        using var client = CreateClient();
        var callback = await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/profile"));
        var oldSessionCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(
                "__Host-ComicManagement.Session=",
                StringComparison.Ordinal))
            .Split(';', 2)[0];
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/me")
        {
            Content = JsonContent.Create(new { displayName = "Updated Session User" }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        var update = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Contains(
            update.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                "__Host-ComicManagement.Session=",
                StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        using var oldSessionClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        oldSessionClient.DefaultRequestHeaders.Add("Cookie", oldSessionCookie);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await oldSessionClient.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_contains_the_profile_contract()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var profilePath = document.GetProperty("paths").GetProperty("/api/v1/me");
        Assert.True(profilePath.TryGetProperty("get", out _));
        Assert.True(profilePath.TryGetProperty("patch", out var patch));
        Assert.True(patch.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(patch.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(patch.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(patch.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(patch.GetProperty("responses").TryGetProperty("409", out _));
    }

    [Fact]
    public async Task Profile_update_requires_authentication_and_csrf()
    {
        using var anonymousClient = CreateClient();
        var anonymousResponse = await anonymousClient.PatchAsJsonAsync(
            "/api/v1/me",
            new { displayName = "Anonymous" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        _factory.TokenEndpoint.SetProfile(
            "google-subject-profile-csrf",
            "CSRF User",
            "profile-csrf@example.com",
            null);
        using var authenticatedClient = CreateClient();
        await CompleteLoginAsync(
            authenticatedClient,
            await BeginLoginAsync(authenticatedClient, "/profile"));

        var missingCsrfResponse = await authenticatedClient.PatchAsJsonAsync(
            "/api/v1/me",
            new { displayName = "Missing CSRF" });

        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);
        var problem = await missingCsrfResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Invalid_or_malformed_profile_update_returns_validation_problem()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-profile-validation",
            "Validation User",
            "profile-validation@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/profile"));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/me")
        {
            Content = JsonContent.Create(new
            {
                displayName = string.Concat(Enumerable.Repeat("\U0001F600", 121)),
            }),
        };
        invalidRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var invalidResponse = await client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        var invalidProblem = await invalidResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", invalidProblem.GetProperty("errorCode").GetString());
        Assert.True(invalidProblem.GetProperty("errors").TryGetProperty("displayName", out _));

        using var malformedRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/me")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        malformedRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var malformedResponse = await client.SendAsync(malformedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        var malformedProblem = await malformedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", malformedProblem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Profile_concurrency_conflict_returns_problem_details_and_does_not_persist()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            forceProfileConflicts: true);
        factory.TokenEndpoint.SetProfile(
            "google-subject-profile-concurrency",
            "Original concurrent name",
            "profile-concurrency@example.com",
            null);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/profile", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/me")
        {
            Content = JsonContent.Create(new { displayName = "Losing update" }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONCURRENCY_CONFLICT", problem.GetProperty("errorCode").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedName = await context.Users
            .Select(user => user.DisplayName)
            .SingleAsync(name => name == "Original concurrent name");
        Assert.Equal("Original concurrent name", persistedName);
    }

    [Fact]
    public async Task First_admin_provisioning_script_is_single_use_and_revokes_old_session()
    {
        const string subject = "google-subject-first-admin-script";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "First Admin Candidate",
            "first-admin@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/"));

        var me = await client.GetAsync("/api/v1/me");
        var profile = await me.Content.ReadFromJsonAsync<JsonElement>();
        var targetUserId = profile.GetProperty("id").GetGuid();
        var script = LoadProvisioningScript(targetUserId);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Database.ExecuteSqlRawAsync(script);
            context.ChangeTracker.Clear();

            var provisioned = await context.Users
                .SingleAsync(user => user.Id == targetUserId);
            Assert.Equal("Admin", provisioned.Role);
            Assert.Equal(2, provisioned.Version);

            var repeatedProvision = await Assert.ThrowsAsync<PostgresException>(
                () => context.Database.ExecuteSqlRawAsync(script));
            Assert.Equal("P0001", repeatedProvision.SqlState);
            Assert.Contains(
                "First Admin already exists",
                repeatedProvision.MessageText,
                StringComparison.Ordinal);
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/i02-test/admin")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_admin_attempts_allow_exactly_one_admin()
    {
        _factory.TokenEndpoint.SetProfile(
            "google-subject-first-admin-race-one",
            "First Admin Candidate One",
            "first-admin-race-one@example.com",
            null);
        using var firstClient = CreateClient();
        await CompleteLoginAsync(firstClient, await BeginLoginAsync(firstClient, "/"));
        var firstProfile = await (await firstClient.GetAsync("/api/v1/me"))
            .Content
            .ReadFromJsonAsync<JsonElement>();
        var firstUserId = firstProfile.GetProperty("id").GetGuid();

        _factory.TokenEndpoint.SetProfile(
            "google-subject-first-admin-race-two",
            "First Admin Candidate Two",
            "first-admin-race-two@example.com",
            null);
        using var secondClient = CreateClient();
        await CompleteLoginAsync(secondClient, await BeginLoginAsync(secondClient, "/"));
        var secondProfile = await (await secondClient.GetAsync("/api/v1/me"))
            .Content
            .ReadFromJsonAsync<JsonElement>();
        var secondUserId = secondProfile.GetProperty("id").GetGuid();

        await using var blockerConnection = new NpgsqlConnection(
            _postgres.GetConnectionString());
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var blockerCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(" +
            "hashtextextended('comic-management:first-admin-provisioning', 0));",
            blockerConnection,
            blockerTransaction))
        {
            await blockerCommand.ExecuteNonQueryAsync();
        }

        await using var firstScope = _factory.Services.CreateAsyncScope();
        await using var secondScope = _factory.Services.CreateAsyncScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = new[]
        {
            ExecuteProvisioningAttemptAsync(
                firstContext,
                LoadProvisioningScript(firstUserId)),
            ExecuteProvisioningAttemptAsync(
                secondContext,
                LoadProvisioningScript(secondUserId)),
        };

        await WaitForAdvisoryLockWaitersAsync(
            blockerConnection,
            blockerTransaction,
            expectedWaiters: 2);
        await blockerTransaction.CommitAsync();

        var failures = await Task.WhenAll(attempts);
        Assert.Single(failures, failure => failure is null);
        var rejectedAttempt = Assert.Single(failures, failure => failure is not null)!;
        Assert.True(
            rejectedAttempt.SqlState is "40001" or "P0001",
            $"Unexpected SQLSTATE: {rejectedAttempt.SqlState}.");

        await using var verificationScope = _factory.Services.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        var candidates = await verificationContext.Users
            .Where(user => user.Id == firstUserId || user.Id == secondUserId)
            .Select(user => new { user.Id, user.Role, user.Version })
            .ToListAsync();

        var provisioned = Assert.Single(
            candidates,
            candidate => candidate.Role == "Admin");
        Assert.Equal(2, provisioned.Version);
        var unchanged = Assert.Single(
            candidates,
            candidate => candidate.Role == "User");
        Assert.Equal(1, unchanged.Version);
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

    private static string LoadProvisioningScript(Guid targetUserId)
    {
        var scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Operations",
            "provision-first-admin.sql");
        var sqlLines = File.ReadAllLines(scriptPath)
            .Where(line => !line.TrimStart().StartsWith('\\'));

        return string.Join(Environment.NewLine, sqlLines)
            .Replace(
                ":'target_user_id'",
                $"'{targetUserId:D}'",
                StringComparison.Ordinal)
            .Replace(":'change_ticket'", "'I02-TEST'", StringComparison.Ordinal)
            .Replace(":'operator_id'", "'test-operator'", StringComparison.Ordinal)
            .Replace(":'reviewer_id'", "'test-reviewer'", StringComparison.Ordinal);
    }

    private static async Task<PostgresException?> ExecuteProvisioningAttemptAsync(
        AppDbContext context,
        string script)
    {
        try
        {
            await context.Database.ExecuteSqlRawAsync(script);
            return null;
        }
        catch (PostgresException exception)
        {
            return exception;
        }
    }

    private static async Task WaitForAdvisoryLockWaitersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int expectedWaiters)
    {
        const int maximumAttempts = 100;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND database = (
                    SELECT oid
                    FROM pg_database
                    WHERE datname = current_database())
                  AND NOT granted;
                """,
                connection,
                transaction);
            var waitingCount = Convert.ToInt32(
                await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
            if (waitingCount >= expectedWaiters)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(
            $"Expected {expectedWaiters} provisioning transactions to wait for the advisory lock.");
    }

    private sealed record LoginFlow(string CallbackUrl);

    private sealed class AuthenticationApplicationFactory(
        string connectionString,
        string securityDirectory,
        int loginPermitLimit = 1000,
        bool forceIdentityConflicts = false,
        bool forceProfileConflicts = false) : WebApplicationFactory<Program>
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
                services.AddControllers()
                    .ConfigureApplicationPartManager(manager =>
                    {
                        var testPart = new AssemblyPart(
                            typeof(I02AuthorizationProbeController).Assembly);
                        if (!manager.ApplicationParts.Any(part =>
                                part.Name == testPart.Name))
                        {
                            manager.ApplicationParts.Add(testPart);
                        }
                    });

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

                if (forceProfileConflicts)
                {
                    services.RemoveAll<IUserProfileStore>();
                    services.AddScoped<IUserProfileStore, AlwaysConflictUserProfileStore>();
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

    private sealed class AlwaysConflictUserProfileStore(AppDbContext dbContext) :
        IUserProfileStore
    {
        public Task<User?> FindByIdAsync(
            Guid userId,
            CancellationToken cancellationToken) => dbContext.Users
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);

        public Task<UserProfileSaveOutcome> SaveChangesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(UserProfileSaveOutcome.ConcurrencyConflict);
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
