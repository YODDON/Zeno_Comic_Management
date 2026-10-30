using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ComicManagement.Catalog.Browsing;
using ComicManagement.Administration.Reporting;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Media;
using ComicManagement.Catalog.Publishing;
using ComicManagement.Catalog.Administration;
using ComicManagement.Persistence;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Profiles;
using ComicManagement.Identity.Users;
using ComicManagement.Identity.Administration;
using ComicManagement.Interaction.Comments;
using ComicManagement.Identity.Events;
using ComicManagement.Missions.Administration;
using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Processing;
using ComicManagement.Missions.Receipts;
using ComicManagement.Missions.Rewards;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Library;
using ComicManagement.Reading.Sessions;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Workflows.Reading;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Queries;
using ComicManagement.Wallet.Reservations;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Events;
using ComicManagement.Billing.Refunds;
using ComicManagement.Api.Billing;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Controllers;
using ComicManagement.Membership.Events;
using ComicManagement.Membership.Memberships;
using ComicManagement.Membership.Plans;
using ComicManagement.Membership.Subscriptions;
using ComicManagement.Interaction.Ratings;
using ComicManagement.Workflows.Interaction;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Text.Encodings.Web;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Api.IntegrationTests;

public sealed class GoogleAuthenticationTests : IAsyncLifetime
{
    private const string CertificatePassword = "i01-test-certificate-password";
    private const string SePayWebhookSecret =
        "0123456789abcdef0123456789abcdef";
    private const string SePayAccountNumber = "0123456789";

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
    public async Task Deployment_health_endpoints_are_anonymous_no_store_and_database_aware()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Contains("no-store", live.Headers.CacheControl?.ToString());
        var livePayload = await live.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", livePayload.GetProperty("status").GetString());

        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("no-store", ready.Headers.CacheControl?.ToString());
        var readyPayload = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", readyPayload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Deployment_readiness_is_fail_closed_and_redacted_when_postgresql_is_unavailable()
    {
        var unavailable = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = 1,
            CommandTimeout = 1,
        };

        await using var factory = new AuthenticationApplicationFactory(
            unavailable.ConnectionString,
            _securityDirectory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/health/ready");

        await AssertRedactedUnhealthyReadinessAsync(response);
    }

    [Fact]
    public async Task Deployment_readiness_is_fail_closed_and_redacted_when_migrations_are_pending()
    {
        var databaseName = $"d01_pending_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE \"{databaseName}\"",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        var pending = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = databaseName,
        };
        await using var factory = new AuthenticationApplicationFactory(
            pending.ConnectionString,
            _securityDirectory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/health/ready");

        await AssertRedactedUnhealthyReadinessAsync(response);
    }

    private static async Task AssertRedactedUnhealthyReadinessAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var rawPayload = await response.Content.ReadAsStringAsync();
        using var payload = JsonDocument.Parse(rawPayload);
        var properties = payload.RootElement.EnumerateObject().ToArray();
        Assert.Single(properties);
        Assert.Equal("status", properties[0].Name);
        Assert.Equal("Unhealthy", properties[0].Value.GetString());
        Assert.DoesNotContain("PostgreSQL", rawPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("migration", rawPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", rawPayload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_composition_registers_the_chapter_page_upload_slice()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<UploadChapterPageHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChapterPageAssetStore>());
    }

    [Fact]
    public async Task Production_authentication_configuration_keeps_cookie_antiforgery_and_oidc_hardening()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var authentication = services
            .GetRequiredService<IOptions<AuthenticationOptions>>()
            .Value;
        Assert.Equal("ComicManagement.Session", authentication.DefaultAuthenticateScheme);
        Assert.Equal("ComicManagement.Session", authentication.DefaultSignInScheme);
        Assert.Equal("ComicManagement.Session", authentication.DefaultChallengeScheme);

        var session = services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get("ComicManagement.Session");
        Assert.Equal("__Host-ComicManagement.Session", session.Cookie.Name);
        Assert.True(session.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, session.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, session.Cookie.SameSite);
        Assert.Equal("/", session.Cookie.Path);
        Assert.True(session.Cookie.IsEssential);
        Assert.False(session.SlidingExpiration);
        Assert.Equal(typeof(SessionCookieEvents), session.EventsType);

        var antiforgery = services
            .GetRequiredService<IOptions<AntiforgeryOptions>>()
            .Value;
        Assert.Equal("X-CSRF-TOKEN", antiforgery.HeaderName);
        Assert.Equal("__Host-ComicManagement.Antiforgery", antiforgery.Cookie.Name);
        Assert.True(antiforgery.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, antiforgery.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, antiforgery.Cookie.SameSite);
        Assert.Equal("/", antiforgery.Cookie.Path);
        Assert.True(antiforgery.Cookie.IsEssential);

        var google = services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get("Google");
        Assert.Equal("ComicManagement.Session", google.SignInScheme);
        Assert.Equal(OpenIdConnectResponseType.Code, google.ResponseType);
        Assert.Equal(OpenIdConnectResponseMode.Query, google.ResponseMode);
        Assert.True(google.UsePkce);
        Assert.False(google.SaveTokens);
        Assert.True(google.RequireHttpsMetadata);
        Assert.Contains("openid", google.Scope);
        Assert.Contains("profile", google.Scope);
        Assert.Contains("email", google.Scope);
        Assert.True(google.CorrelationCookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, google.CorrelationCookie.SecurePolicy);
        Assert.Equal(SameSiteMode.None, google.CorrelationCookie.SameSite);
        Assert.True(google.NonceCookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, google.NonceCookie.SecurePolicy);
        Assert.Equal(SameSiteMode.None, google.NonceCookie.SameSite);
        Assert.True(google.TokenValidationParameters.ValidateIssuer);
        Assert.True(google.TokenValidationParameters.ValidateAudience);
        Assert.True(google.TokenValidationParameters.ValidateLifetime);
        Assert.True(google.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.Equal(typeof(GoogleOpenIdConnectEvents), google.EventsType);
    }

    [Fact]
    public async Task Production_composition_registers_the_catalog_publication_slice()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CatalogPublicationHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICatalogPublicationStore>());
    }

    [Fact]
    public async Task Production_composition_registers_the_public_catalog_slice()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PublicCatalogHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPublicCatalogStore>());
    }

    [Fact]
    public async Task Production_composition_registers_the_reading_access_slice()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ChapterAccessHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChapterAccessProjectionStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ChapterUnlockQuoteHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChapterUnlockQuoteStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ChapterUnlockHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChapterUnlockStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChapterPageContentProvider>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PublicChapterListWorkflow>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReadingSessionHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReadingSessionStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<LibraryHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILibraryStore>());
    }

    [Fact]
    public async Task Production_composition_registers_the_wallet_query_slice()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<WalletQueryHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IWalletQueryStore>());
    }

    [Fact]
    public async Task Production_composition_registers_the_topup_order_slice()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TopUpOrderHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITopUpOrderStore>());
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            scope.ServiceProvider.GetRequiredService<TopUpOrderPolicy>().TimeToLive);
    }

    [Fact]
    public void Production_composition_fails_fast_for_non_positive_topup_ttl()
    {
        using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            topUpOrderTtlMinutes: 0);

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Production_composition_binds_configured_topup_ttl()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            topUpOrderTtlMinutes: 45);
        await using var scope = factory.Services.CreateAsyncScope();

        Assert.Equal(
            TimeSpan.FromMinutes(45),
            scope.ServiceProvider.GetRequiredService<TopUpOrderPolicy>().TimeToLive);
    }

    [Fact]
    public async Task Production_composition_registers_membership_and_defaults_vip_ttl()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<VipMembershipHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IVipMembershipStore>());
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            scope.ServiceProvider.GetRequiredService<VipOrderPolicy>().TimeToLive);
    }

    [Fact]
    public void Production_composition_fails_fast_for_non_positive_vip_ttl()
    {
        using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            vipOrderTtlMinutes: 0);

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Vip_checkout_requires_authentication_csrf_and_permanent_idempotency()
    {
        using var anonymousClient = CreateClient();
        using var anonymous = VipCheckoutRequest("vip-anonymous");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymousClient.SendAsync(anonymous)).StatusCode);

        _factory.TokenEndpoint.SetProfile(
            $"vip-checkout-{Guid.NewGuid():N}",
            "VIP Checkout User",
            null,
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/vip"));

        var planResponse = await client.GetAsync("/api/v1/membership/plan");
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
        var plan = await planResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(VipPlan.InitialPlanId, plan.GetProperty("planId").GetGuid());
        Assert.Equal(29_000, plan.GetProperty("priceVnd").GetInt64());
        Assert.Equal(720, plan.GetProperty("durationHours").GetInt32());
        Assert.Equal(20, plan.GetProperty("discountPercent").GetInt32());

        var me = await client.GetAsync("/api/v1/membership/me");
        var membership = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(membership.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, membership.GetProperty("expiresAt").ValueKind);

        using var missingCsrf = VipCheckoutRequest("vip-missing-csrf");
        await AssertStandardizedErrorAsync(
            await client.SendAsync(missingCsrf),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        var profile = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(profile.Headers.GetValues("X-CSRF-TOKEN"));

        using var missingHeader = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/membership/checkout")
        {
            Content = JsonContent.Create(new { planId = VipPlan.InitialPlanId }),
        };
        missingHeader.Headers.Add("X-CSRF-TOKEN", csrfToken);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(missingHeader),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var emptyHeader = VipCheckoutRequest(string.Empty, csrfToken);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(emptyHeader),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var longHeader = VipCheckoutRequest(
            new string('x', PaymentOrder.MaximumIdempotencyKeyLength + 1),
            csrfToken);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(longHeader),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var firstRequest = VipCheckoutRequest("vip-idempotency", csrfToken);
        var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(29_000, first.GetProperty("amountVnd").GetInt64());
        Assert.Equal(720, first.GetProperty("durationHours").GetInt32());
        Assert.Equal("Pending", first.GetProperty("status").GetString());

        using var replayRequest = VipCheckoutRequest("vip-idempotency", csrfToken);
        var replayResponse = await client.SendAsync(replayRequest);
        var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("orderId").GetGuid(), replay.GetProperty("orderId").GetGuid());

        using var conflictRequest = VipCheckoutRequest(
            "vip-idempotency",
            csrfToken,
            Guid.NewGuid());
        await AssertStandardizedErrorAsync(
            await client.SendAsync(conflictRequest),
            HttpStatusCode.Conflict,
            "IDEMPOTENCY_CONFLICT");

        var clientExpiry = DateTimeOffset.UtcNow.AddYears(1);
        using var overrideRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/membership/checkout")
        {
            Content = JsonContent.Create(new
            {
                planId = VipPlan.InitialPlanId,
                expiresAt = clientExpiry,
            }),
        };
        overrideRequest.Headers.Add("Idempotency-Key", "vip-server-owned-expiry");
        overrideRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var overrideResponse = await client.SendAsync(overrideRequest);
        Assert.Equal(HttpStatusCode.Created, overrideResponse.StatusCode);
        var overrideBody = await overrideResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(
            clientExpiry,
            overrideBody.GetProperty("expiresAt").GetDateTimeOffset());

        await using (var expiryScope = _factory.Services.CreateAsyncScope())
        {
            var expiryContext = expiryScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await expiryContext.PaymentOrders
                .Where(order => order.Id == first.GetProperty("orderId").GetGuid())
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(order => order.CreatedAt, expiredAt.AddMinutes(-30))
                    .SetProperty(order => order.ExpiresAt, expiredAt));
        }

        using var expiredReplayRequest = VipCheckoutRequest(
            "vip-idempotency",
            csrfToken);
        var expiredReplayResponse = await client.SendAsync(expiredReplayRequest);
        Assert.Equal(HttpStatusCode.Created, expiredReplayResponse.StatusCode);
        var expiredReplay = await expiredReplayResponse.Content
            .ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Expired", expiredReplay.GetProperty("status").GetString());
        Assert.Equal(
            first.GetProperty("orderId").GetGuid(),
            expiredReplay.GetProperty("orderId").GetGuid());

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await context.PaymentOrders.SingleAsync(candidate =>
            candidate.Id == first.GetProperty("orderId").GetGuid());
        Assert.Equal(order.CreatedAt.AddMinutes(30), order.ExpiresAt);
        Assert.Equal(VipPlan.InitialPlanId, order.VipPlanId);
        Assert.Equal(20, order.VipDiscountPercentSnapshot);
        Assert.Equal(TopUpOrderStatuses.Expired, order.OrderStatus);
        Assert.NotNull(order.ExpiredAt);
    }

    [Fact]
    public async Task Vip_checkout_webhook_replay_and_membership_query_run_through_real_http_pipeline()
    {
        await using var factory = CreateSePayFactory();
        factory.TokenEndpoint.SetProfile(
            $"vip-http-{Guid.NewGuid():N}",
            "VIP HTTP User",
            null,
            null);
        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/vip", factory.TokenEndpoint));
        var profile = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(profile.Headers.GetValues("X-CSRF-TOKEN"));

        using var checkoutRequest = VipCheckoutRequest("vip-http-flow", csrfToken);
        var checkoutResponse = await client.SendAsync(checkoutRequest);
        Assert.Equal(HttpStatusCode.Created, checkoutResponse.StatusCode);
        var checkout = await checkoutResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = checkout.GetProperty("orderId").GetGuid();
        var amountVnd = checkout.GetProperty("amountVnd").GetInt64();

        string orderCode;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            orderCode = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PaymentOrders
                .Where(order => order.Id == orderId)
                .Select(order => order.OrderCode)
                .SingleAsync();
        }

        var providerId = $"89{Random.Shared.Next(100_000, 999_999)}";
        using var providerClient = CreateClient(factory);
        using (var webhook = SePayRequest(providerId, amountVnd, paymentCode: orderCode))
        {
            Assert.Equal(HttpStatusCode.OK, (await providerClient.SendAsync(webhook)).StatusCode);
        }

        using (var replay = SePayRequest(providerId, amountVnd, paymentCode: orderCode))
        {
            Assert.Equal(HttpStatusCode.OK, (await providerClient.SendAsync(replay)).StatusCode);
        }

        var membershipResponse = await client.GetAsync("/api/v1/membership/me");
        Assert.Equal(HttpStatusCode.OK, membershipResponse.StatusCode);
        var membership = await membershipResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(membership.GetProperty("isActive").GetBoolean());
        var responseExpiry = membership.GetProperty("expiresAt").GetDateTimeOffset();

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var subscription = await verification.VipSubscriptions.SingleAsync();
        Assert.Equal(responseExpiry, subscription.ExpiresAt);
        Assert.Equal(1, subscription.Version);
        Assert.Equal(1, await verification.VipTransactions.CountAsync(transaction =>
            transaction.PaymentOrderId == orderId && transaction.SubscriptionVersion == 1));
        var providerTransaction = await verification.ProviderTransactions.SingleAsync(transaction =>
            transaction.ProviderTransactionId == providerId);
        Assert.Equal(
            ProviderTransactionProcessingStatuses.Fulfilled,
            providerTransaction.ProcessingStatus);
        Assert.Equal(2, await verification.WebhookDeliveries.CountAsync(delivery =>
            delivery.ProviderTransactionId == providerTransaction.Id));
        Assert.Equal(1, await verification.OutboxMessages.CountAsync(message =>
            message.EventType == MembershipEventTypes.VipMembershipActivated));
    }

    [Fact]
    public async Task Vip_checkout_is_limited_to_ten_requests_per_minute_per_user()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        factory.TokenEndpoint.SetProfile(
            $"vip-rate-{Guid.NewGuid():N}",
            "VIP Rate User",
            null,
            null);
        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/vip", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var request = VipCheckoutRequest($"vip-rate-{attempt}", csrfToken);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
        }

        using var rejectedRequest = VipCheckoutRequest("vip-rate-11", csrfToken);
        var rejected = await client.SendAsync(rejectedRequest);
        await AssertStandardizedErrorAsync(
            rejected,
            HttpStatusCode.TooManyRequests,
            "RATE_LIMITED");
        Assert.True(rejected.Headers.RetryAfter is not null ||
                    rejected.Headers.TryGetValues("Retry-After", out _));
    }

    [Fact]
    public async Task Vip_plan_price_update_requires_admin_csrf_and_preserves_existing_snapshot()
    {
        const string subject = "google-subject-v01-admin";
        _factory.TokenEndpoint.SetProfile(subject, "VIP Admin", null, null);
        using var userClient = CreateClient();
        await CompleteLoginAsync(userClient, await BeginLoginAsync(userClient, "/vip"));
        var userProfile = await userClient.GetAsync("/api/v1/me");
        var userCsrf = Assert.Single(userProfile.Headers.GetValues("X-CSRF-TOKEN"));
        using var oldCheckout = VipCheckoutRequest("vip-old-price", userCsrf);
        var oldResponse = await userClient.SendAsync(oldCheckout);
        var oldOrder = await oldResponse.Content.ReadFromJsonAsync<JsonElement>();

        using var forbiddenPatch = new HttpRequestMessage(
            HttpMethod.Patch,
            "/api/v1/admin/vip-plan")
        {
            Content = JsonContent.Create(new { priceVnd = 35_000, expectedVersion = 1 }),
        };
        forbiddenPatch.Headers.Add("X-CSRF-TOKEN", userCsrf);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await userClient.SendAsync(forbiddenPatch)).StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Users
                .Where(user => context.GoogleIdentities.Any(identity =>
                    identity.UserId == user.Id && identity.GoogleSubject == subject))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(user => user.Role, IdentityRoles.Admin)
                    .SetProperty(user => user.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(user => user.Version, user => user.Version + 1));
        }

        using var adminClient = CreateClient();
        await CompleteLoginAsync(adminClient, await BeginLoginAsync(adminClient, "/admin/vip"));
        var adminProfile = await adminClient.GetAsync("/api/v1/me");
        var adminCsrf = Assert.Single(adminProfile.Headers.GetValues("X-CSRF-TOKEN"));
        using var missingCsrf = new HttpRequestMessage(
            HttpMethod.Patch,
            "/api/v1/admin/vip-plan")
        {
            Content = JsonContent.Create(new { priceVnd = 35_000, expectedVersion = 1 }),
        };
        await AssertStandardizedErrorAsync(
            await adminClient.SendAsync(missingCsrf),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            "/api/v1/admin/vip-plan")
        {
            Content = JsonContent.Create(new { priceVnd = 35_000, expectedVersion = 1 }),
        };
        patchRequest.Headers.Add("X-CSRF-TOKEN", adminCsrf);
        var patchedResponse = await adminClient.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patchedResponse.StatusCode);
        var patched = await patchedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(35_000, patched.GetProperty("priceVnd").GetInt64());
        Assert.Equal(2, patched.GetProperty("version").GetInt64());

        using var stalePatch = new HttpRequestMessage(
            HttpMethod.Patch,
            "/api/v1/admin/vip-plan")
        {
            Content = JsonContent.Create(new { priceVnd = 36_000, expectedVersion = 1 }),
        };
        stalePatch.Headers.Add("X-CSRF-TOKEN", adminCsrf);
        await AssertStandardizedErrorAsync(
            await adminClient.SendAsync(stalePatch),
            HttpStatusCode.Conflict,
            "CONCURRENCY_CONFLICT");

        using var newCheckout = VipCheckoutRequest("vip-new-price", adminCsrf);
        var newResponse = await adminClient.SendAsync(newCheckout);
        var newOrder = await newResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(35_000, newOrder.GetProperty("amountVnd").GetInt64());

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            29_000,
            (await verify.PaymentOrders.FindAsync(oldOrder.GetProperty("orderId").GetGuid()))!
            .AmountVnd);
    }

    [Fact]
    public async Task Admin_mission_workflow_requires_admin_csrf_and_supports_revision_lifecycle()
    {
        using var anonymousClient = CreateClient();
        await AssertStandardizedErrorAsync(
            await anonymousClient.GetAsync("/api/v1/admin/missions"),
            HttpStatusCode.Unauthorized,
            "AUTH_REQUIRED");

        const string subject = "google-subject-m01-admin";
        _factory.TokenEndpoint.SetProfile(subject, "Mission Admin", null, null);
        using var userClient = CreateClient();
        await CompleteLoginAsync(userClient, await BeginLoginAsync(userClient, "/admin/missions"));
        var userProfile = await userClient.GetAsync("/api/v1/me");
        var userCsrf = Assert.Single(userProfile.Headers.GetValues("X-CSRF-TOKEN"));
        using var forbiddenCreate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/missions")
        {
            Content = JsonContent.Create(new
            {
                code = "read-one-chapter",
                name = "Read one chapter",
                description = string.Empty,
                actionType = "ChapterReadQualified",
                cycleType = "Daily",
            }),
        };
        forbiddenCreate.Headers.Add("X-CSRF-TOKEN", userCsrf);
        await AssertStandardizedErrorAsync(
            await userClient.SendAsync(forbiddenCreate),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Users
                .Where(user => context.GoogleIdentities.Any(identity =>
                    identity.UserId == user.Id && identity.GoogleSubject == subject))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(user => user.Role, IdentityRoles.Admin)
                    .SetProperty(user => user.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(user => user.Version, user => user.Version + 1));
        }

        using var adminClient = CreateClient();
        await CompleteLoginAsync(adminClient, await BeginLoginAsync(adminClient, "/admin/missions"));
        var adminProfile = await adminClient.GetAsync("/api/v1/me");
        var csrf = Assert.Single(adminProfile.Headers.GetValues("X-CSRF-TOKEN"));

        var missingCsrf = await adminClient.PostAsJsonAsync(
            "/api/v1/admin/missions",
            new
            {
                code = "read-one-chapter",
                name = "Read one chapter",
                description = string.Empty,
                actionType = "ChapterReadQualified",
                cycleType = "Daily",
            });
        await AssertStandardizedErrorAsync(
            missingCsrf,
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/missions")
        {
            Content = JsonContent.Create(new
            {
                code = "read-one-chapter",
                name = "Read one chapter",
                description = string.Empty,
                actionType = "ChapterReadQualified",
                cycleType = "Daily",
            }),
        };
        createRequest.Headers.Add("X-CSRF-TOKEN", csrf);
        var createdResponse = await adminClient.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var missionId = created.GetProperty("id").GetGuid();
        Assert.Equal("Draft", created.GetProperty("status").GetString());
        Assert.Equal(1, created.GetProperty("version").GetInt64());

        using var revisionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/admin/missions/{missionId:D}/revisions")
        {
            Content = JsonContent.Create(new
            {
                targetCount = 1,
                rewardBonusCoins = 25,
                repeatPolicy = "UniqueSubjectPerPeriod",
                effectiveFrom = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
                effectiveUntil = (DateTimeOffset?)null,
                configurationSnapshot = new { policy = "m01" },
            }),
        };
        revisionRequest.Headers.Add("X-CSRF-TOKEN", csrf);
        var revisionResponse = await adminClient.SendAsync(revisionRequest);
        Assert.Equal(HttpStatusCode.Created, revisionResponse.StatusCode);
        var revision = await revisionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var revisionId = revision.GetProperty("id").GetGuid();
        Assert.Equal(1, revision.GetProperty("revisionNumber").GetInt32());

        using var activateRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/admin/missions/{missionId:D}/activate")
        {
            Content = JsonContent.Create(new { revisionId, expectedVersion = 1 }),
        };
        activateRequest.Headers.Add("X-CSRF-TOKEN", csrf);
        var activateResponse = await adminClient.SendAsync(activateRequest);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        var active = await activateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", active.GetProperty("status").GetString());
        Assert.Equal(revisionId, active.GetProperty("activeRevisionId").GetGuid());
        Assert.Equal(2, active.GetProperty("version").GetInt64());

        var listed = await adminClient.GetFromJsonAsync<JsonElement>("/api/v1/admin/missions");
        Assert.Contains(
            listed.EnumerateArray(),
            mission => mission.GetProperty("id").GetGuid() == missionId);

        using var staleActivation = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/admin/missions/{missionId:D}/activate")
        {
            Content = JsonContent.Create(new { revisionId, expectedVersion = 1 }),
        };
        staleActivation.Headers.Add("X-CSRF-TOKEN", csrf);
        await AssertStandardizedErrorAsync(
            await adminClient.SendAsync(staleActivation),
            HttpStatusCode.Conflict,
            "CONCURRENCY_CONFLICT");

        using var deactivateRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/admin/missions/{missionId:D}/deactivate")
        {
            Content = JsonContent.Create(new { expectedVersion = 2 }),
        };
        deactivateRequest.Headers.Add("X-CSRF-TOKEN", csrf);
        var deactivateResponse = await adminClient.SendAsync(deactivateRequest);
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);
        var inactive = await deactivateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", inactive.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, inactive.GetProperty("activeRevisionId").ValueKind);
    }

    [Fact]
    public async Task Admin_mission_mutations_are_limited_to_twenty_per_minute_per_admin()
    {
        var (adminClient, csrf) = await CreateAdminClientAsync(
            "google-subject-m01-rate-limit");
        using (adminClient)
        {
            for (var index = 0; index < 20; index++)
            {
                using var request = CreateMissionRequest(
                    $"rate-limited-mission-{index}",
                    csrf);
                Assert.Equal(
                    HttpStatusCode.Created,
                    (await adminClient.SendAsync(request)).StatusCode);
            }

            using var rejectedRequest = CreateMissionRequest(
                "rate-limited-mission-rejected",
                csrf);
            await AssertStandardizedErrorAsync(
                await adminClient.SendAsync(rejectedRequest),
                HttpStatusCode.TooManyRequests,
                "RATE_LIMITED");

            Assert.Equal(
                HttpStatusCode.OK,
                (await adminClient.GetAsync("/api/v1/admin/missions")).StatusCode);
        }
    }

    [Fact]
    public async Task Admin_mission_contract_rejects_numeric_enum_values()
    {
        var (adminClient, csrf) = await CreateAdminClientAsync(
            "google-subject-m01-string-enums");
        using (adminClient)
        {
            using var numericAction = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/v1/admin/missions")
            {
                Content = JsonContent.Create(new
                {
                    code = "numeric-action",
                    name = "Numeric action",
                    description = string.Empty,
                    actionType = 1,
                    cycleType = "Daily",
                }),
            };
            numericAction.Headers.Add("X-CSRF-TOKEN", csrf);
            await AssertStandardizedErrorAsync(
                await adminClient.SendAsync(numericAction),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");

            using var numericCycle = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/v1/admin/missions")
            {
                Content = JsonContent.Create(new
                {
                    code = "numeric-cycle",
                    name = "Numeric cycle",
                    description = string.Empty,
                    actionType = "ChapterReadQualified",
                    cycleType = 2,
                }),
            };
            numericCycle.Headers.Add("X-CSRF-TOKEN", csrf);
            await AssertStandardizedErrorAsync(
                await adminClient.SendAsync(numericCycle),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");

            using var validMission = CreateMissionRequest("numeric-repeat-policy", csrf);
            var createResponse = await adminClient.SendAsync(validMission);
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var mission = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

            using var numericRepeat = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/admin/missions/{mission.GetProperty("id").GetGuid():D}/revisions")
            {
                Content = JsonContent.Create(new
                {
                    targetCount = 1,
                    rewardBonusCoins = 10,
                    repeatPolicy = 2,
                    effectiveFrom = new DateTimeOffset(
                        2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
                    configurationSnapshot = new { },
                }),
            };
            numericRepeat.Headers.Add("X-CSRF-TOKEN", csrf);
            await AssertStandardizedErrorAsync(
                await adminClient.SendAsync(numericRepeat),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }
    }

    [Fact]
    public async Task Successful_google_login_persists_and_processes_a_daily_login_action_receipt()
    {
        const string subject = "google-subject-m01-daily-login";
        _factory.TokenEndpoint.SetProfile(subject, "Daily Login", null, null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/missions"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await context.GoogleIdentities
            .Where(identity => identity.GoogleSubject == subject)
            .Select(identity => identity.UserId)
            .SingleAsync();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        for (var attempt = 0; attempt < 50 && !await context.ActionReceipts.AnyAsync(candidate =>
                 candidate.SourceEventType == IdentityEventTypes.UserLoggedIn &&
                 candidate.UserId == userId); attempt++)
        {
            await processor.ProcessBatchAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        ActionReceipt? receipt = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            context.ChangeTracker.Clear();
            receipt = await context.ActionReceipts
                .AsNoTracking()
                .Where(candidate =>
                    candidate.SourceEventType == IdentityEventTypes.UserLoggedIn &&
                    candidate.UserId == userId)
                .SingleOrDefaultAsync();
            if (receipt?.ProcessingStatus == ActionReceiptStatuses.Processed)
            {
                break;
            }

            if (receipt is not null)
            {
                await scope.ServiceProvider.GetRequiredService<MissionReceiptProcessor>()
                    .ProcessAsync(receipt.Id);
            }
            else
            {
                await processor.ProcessBatchAsync(CancellationToken.None);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        var loginMessages = await context.OutboxMessages
            .AsNoTracking()
            .Where(message => message.EventType == IdentityEventTypes.UserLoggedIn)
            .OrderByDescending(message => message.OccurredAt)
            .Take(10)
            .Select(message => new
            {
                message.Status,
                message.AttemptCount,
                message.LastErrorRedacted,
                message.Payload,
            })
            .ToArrayAsync();
        Assert.True(
            receipt is not null,
            $"DailyLogin receipt missing. Outbox: {JsonSerializer.Serialize(loginMessages)}");

        Assert.Null(receipt!.SubjectId);
        Assert.Equal(ActionReceiptStatuses.Processed, receipt.ProcessingStatus);
        Assert.NotEqual(Guid.Empty, receipt.SourceEventId);
    }

    [Fact]
    public async Task Daily_login_reward_pipeline_persists_bonus_and_exposes_user_mission_queries()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = MissionDefinition.Create(
            Guid.NewGuid(),
            $"daily-login-{Guid.NewGuid():N}",
            "Daily login",
            "Login once today",
            MissionActionTypes.DailyLogin,
            MissionCycleTypes.Daily,
            now.AddMinutes(-5));
        var revision = MissionRevision.Create(
            Guid.NewGuid(),
            definition.Id,
            1,
            1,
            25,
            MissionRepeatPolicies.OncePerVietnamDate,
            now.AddMinutes(-5),
            null,
            "{}");
        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.MissionDefinitions.Add(definition);
            context.MissionRevisions.Add(revision);
            await context.SaveChangesAsync();
            definition.Activate(revision, 1);
            context.MissionActivations.Add(MissionActivation.Create(
                Guid.NewGuid(), definition.Id, revision.Id, 1, now.AddMinutes(-5)));
            await context.SaveChangesAsync();
        }

        const string subject = "google-subject-m02-daily-reward";
        _factory.TokenEndpoint.SetProfile(subject, "M02 User", null, null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/missions"));

        Guid userId;
        await using (var identityScope = _factory.Services.CreateAsyncScope())
        {
            var context = identityScope.ServiceProvider.GetRequiredService<AppDbContext>();
            userId = await context.GoogleIdentities
                .Where(identity => identity.GoogleSubject == subject)
                .Select(identity => identity.UserId)
                .SingleAsync();
        }

        MissionRewardGrant? grant = null;
        for (var attempt = 0; attempt < 100 && grant is null; attempt++)
        {
            await using var processingScope = _factory.Services.CreateAsyncScope();
            var services = processingScope.ServiceProvider;
            await services.GetRequiredService<OutboxBatchProcessor>()
                .ProcessBatchAsync(CancellationToken.None);
            await services.GetRequiredService<MissionReceiptProcessor>()
                .ProcessNextAsync(CancellationToken.None);
            var context = services.GetRequiredService<AppDbContext>();
            context.ChangeTracker.Clear();
            grant = await context.MissionRewardGrants.AsNoTracking()
                .SingleOrDefaultAsync(item => item.UserId == userId);
            if (grant is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }

        Assert.NotNull(grant);
        Assert.Equal(MissionRewardOutcomes.Granted, grant.Outcome);
        Assert.Equal(25, grant.BonusCoins);

        var missionsResponse = await client.GetAsync("/api/v1/missions");
        Assert.Equal(HttpStatusCode.OK, missionsResponse.StatusCode);
        Assert.Equal("no-store", missionsResponse.Headers.CacheControl?.ToString());
        var missions = await missionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var mission = Assert.Single(missions.EnumerateArray(), item =>
            item.GetProperty("missionId").GetGuid() == definition.Id);
        Assert.Equal(revision.Id, mission.GetProperty("revisionId").GetGuid());
        Assert.Equal(1, mission.GetProperty("progressCount").GetInt32());
        Assert.Equal("Completed", mission.GetProperty("progressStatus").GetString());
        Assert.Equal("Granted", mission.GetProperty("rewardStatus").GetString());

        var dailyLimit = await client.GetFromJsonAsync<JsonElement>(
            "/api/v1/missions/daily-limit");
        Assert.Equal(300, dailyLimit.GetProperty("limitCoins").GetInt64());
        Assert.Equal(25, dailyLimit.GetProperty("grantedCoins").GetInt64());
        Assert.Equal(275, dailyLimit.GetProperty("remainingCoins").GetInt64());

        var rewards = await client.GetFromJsonAsync<JsonElement>(
            "/api/v1/missions/rewards?limit=1");
        var reward = Assert.Single(rewards.GetProperty("items").EnumerateArray());
        Assert.Equal(grant.Id, reward.GetProperty("rewardGrantId").GetGuid());
        Assert.Equal(25, reward.GetProperty("grantedBonusCoins").GetInt64());
        Assert.Equal(JsonValueKind.Null, rewards.GetProperty("nextCursor").ValueKind);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(25, await verify.Wallets
            .Where(item => item.UserId == userId)
            .Select(item => item.BonusAvailable)
            .SingleAsync());
        Assert.Single(await verify.WalletOperations.Where(item =>
            item.UserId == userId &&
            item.OperationType == WalletOperationTypes.MissionReward &&
            item.SourceType == WalletOperationSourceTypes.RewardGrant)
            .ToArrayAsync());
    }

    [Fact]
    public async Task Mission_user_queries_require_authentication_and_validate_reward_pagination()
    {
        using var anonymous = CreateClient();
        foreach (var path in new[]
                 {
                     "/api/v1/missions",
                     "/api/v1/missions/daily-limit",
                     "/api/v1/missions/rewards",
                 })
        {
            await AssertStandardizedErrorAsync(
                await anonymous.GetAsync(path),
                HttpStatusCode.Unauthorized,
                "AUTH_REQUIRED");
        }

        const string subject = "google-subject-m02-query-validation";
        _factory.TokenEndpoint.SetProfile(subject, "M02 Query User", null, null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/missions"));
        await AssertStandardizedErrorAsync(
            await client.GetAsync("/api/v1/missions/rewards?limit=101"),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");
        await AssertStandardizedErrorAsync(
            await client.GetAsync("/api/v1/missions/rewards?cursor=invalid"),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Topup_creation_requires_authentication_csrf_and_persistent_idempotency()
    {
        using var anonymousClient = CreateClient();
        using var anonymousRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/billing/topups")
        {
            Content = JsonContent.Create(new { amountVnd = 29_000 }),
        };
        anonymousRequest.Headers.Add("Idempotency-Key", "anonymous-key");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymousClient.SendAsync(anonymousRequest)).StatusCode);

        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/wallet"));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        using var missingCsrf = TopUpRequest("topup-key", 29_000);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.SendAsync(missingCsrf)).StatusCode);

        using var firstRequest = TopUpRequest("topup-key", 29_000, csrfToken);
        var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal("no-store", firstResponse.Headers.CacheControl!.ToString());
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2_900, first.GetProperty("paidCoinsExpected").GetInt64());
        Assert.Equal("Pending", first.GetProperty("status").GetString());
        Assert.Equal(
            "ProviderSpecific",
            first.GetProperty("paymentInstructions").GetProperty("type").GetString());

        using var replayRequest = TopUpRequest("topup-key", 29_000, csrfToken);
        var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("orderId").GetGuid(), replay.GetProperty("orderId").GetGuid());

        using var conflictRequest = TopUpRequest("topup-key", 30_000, csrfToken);
        var conflictResponse = await client.SendAsync(conflictRequest);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(conflict.GetProperty("traceId").GetString()));

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orderId = first.GetProperty("orderId").GetGuid();
        var order = await context.PaymentOrders.SingleAsync(value => value.Id == orderId);
        Assert.Equal(order.CreatedAt.AddMinutes(30), order.ExpiresAt);
        Assert.Equal(
            1,
            await context.PaymentOrders.CountAsync(value =>
                value.IdempotencyKey == "topup-key"));
    }

    [Theory]
    [InlineData(9_000)]
    [InlineData(10_500)]
    [InlineData(2_001_000)]
    public async Task Topup_creation_rejects_invalid_amounts(long amountVnd)
    {
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/wallet"));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        using var request = TopUpRequest($"invalid-{amountVnd}", amountVnd, csrfToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Topup_validation_csrf_and_server_owned_expiry_use_standardized_errors()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        factory.TokenEndpoint.SetProfile(
            $"billing-validation-{Guid.NewGuid():N}",
            "Billing Validation User",
            null,
            null);
        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/wallet", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        using var missingCsrf = TopUpRequest("missing-csrf", 10_000);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(missingCsrf),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var missingHeader = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/billing/topups")
        {
            Content = JsonContent.Create(new { amountVnd = 10_000 }),
        };
        missingHeader.Headers.Add("X-CSRF-TOKEN", csrfToken);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(missingHeader),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var emptyHeader = TopUpRequest(string.Empty, 10_000, csrfToken);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(emptyHeader),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var longHeader = TopUpRequest(
            new string('x', PaymentOrder.MaximumIdempotencyKeyLength + 1),
            10_000,
            csrfToken);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(longHeader),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        var clientExpiry = DateTimeOffset.UtcNow.AddYears(1);
        using var overrideRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/billing/topups")
        {
            Content = JsonContent.Create(new { amountVnd = 10_000, expiresAt = clientExpiry }),
        };
        overrideRequest.Headers.Add("Idempotency-Key", "server-owned-expiry");
        overrideRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var overrideResponse = await client.SendAsync(overrideRequest);
        Assert.Equal(HttpStatusCode.Created, overrideResponse.StatusCode);
        var body = await overrideResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(clientExpiry, body.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Topup_create_is_limited_to_ten_requests_per_minute_per_user()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        factory.TokenEndpoint.SetProfile(
            $"billing-rate-{Guid.NewGuid():N}",
            "Billing Rate User",
            null,
            null);
        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/wallet", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var request = TopUpRequest($"rate-{attempt}", 10_000, csrfToken);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
        }

        using var rejectedRequest = TopUpRequest("rate-11", 10_000, csrfToken);
        var rejected = await client.SendAsync(rejectedRequest);
        await AssertStandardizedErrorAsync(
            rejected,
            HttpStatusCode.TooManyRequests,
            "RATE_LIMITED");
        Assert.True(rejected.Headers.RetryAfter is not null ||
                    rejected.Headers.TryGetValues("Retry-After", out _));
    }

    [Fact]
    public async Task Topup_http_replay_after_expiry_returns_and_persists_the_original_order()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        factory.TokenEndpoint.SetProfile(
            $"billing-expiry-{Guid.NewGuid():N}",
            "Billing Expiry User",
            null,
            null);
        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/wallet", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        using var createRequest = TopUpRequest("expired-http-replay", 10_000, csrfToken);
        var createdResponse = await client.SendAsync(createRequest);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = created.GetProperty("orderId").GetGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE billing.payment_orders
                SET created_at = {now.AddMinutes(-31)}, expires_at = {now.AddMinutes(-1)}
                WHERE id = {orderId};
                """);
        }

        using var replayRequest = TopUpRequest("expired-http-replay", 10_000, csrfToken);
        var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(orderId, replay.GetProperty("orderId").GetGuid());
        Assert.Equal("Expired", replay.GetProperty("status").GetString());

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verification.PaymentOrders.SingleAsync(order => order.Id == orderId);
        Assert.Equal(TopUpOrderStatuses.Expired, persisted.OrderStatus);
        Assert.NotNull(persisted.ExpiredAt);
    }

    [Fact]
    public async Task Topup_create_returns_forbidden_for_authenticated_principal_without_user_role()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            useInsufficientRoleTestAuthentication: true);
        using var client = CreateClient(factory);
        using var request = TopUpRequest("insufficient-role", 10_000);

        await AssertStandardizedErrorAsync(
            await client.SendAsync(request),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");
    }

    [Fact]
    public void Refund_security_uses_versioned_hmac_and_protection_survives_restart_and_rotation()
    {
        var v1Key = Encoding.UTF8.GetBytes("rf01-test-fingerprint-key-v1-32!");
        var v2Key = Encoding.UTF8.GetBytes("rf01-test-fingerprint-key-v2-32!");
        var v1Settings = RefundSecuritySettingsFor(
            "v1",
            "v1",
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["v1"] = v1Key });
        var canonical = "Refund|order=abc|account=10:0123456789|holder=12:NGUYEN VAN A";
        var v1Service = new RefundRequestFingerprintService(v1Settings);
        var first = v1Service.CreateFingerprints(canonical);
        var replay = v1Service.CreateFingerprints(canonical);
        var changed = v1Service.CreateFingerprints(canonical.Replace("6789", "6790"));

        Assert.Equal(first.CurrentFingerprint, replay.CurrentFingerprint);
        Assert.NotEqual(first.CurrentFingerprint, changed.CurrentFingerprint);
        Assert.NotEqual(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant(),
            first.CurrentFingerprint);

        var v2Settings = RefundSecuritySettingsFor(
            "v2",
            "v2",
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["v1"] = v1Key,
                ["v2"] = v2Key,
            });
        var rotated = new RefundRequestFingerprintService(v2Settings)
            .CreateFingerprints(canonical);
        Assert.Equal(first.CurrentFingerprint, rotated.CandidatesByVersion["v1"]);
        Assert.NotEqual(first.CurrentFingerprint, rotated.CurrentFingerprint);

        var keyDirectory = Directory.CreateTempSubdirectory("rf01-protection-");
        var keyBOnlyDirectory = Directory.CreateTempSubdirectory("rf01-protection-b-");
        try
        {
            var now = DateTimeOffset.UtcNow;
            Guid masterKeyA;
            ProtectedRefundRecipient protectedRecipient;
            using (var firstServices = CreateRefundDataProtectionServices(keyDirectory))
            {
                masterKeyA = firstServices.GetRequiredService<IKeyManager>()
                    .CreateNewKey(now.AddMinutes(-2), now.AddDays(90)).KeyId;
                var firstProtector = new RefundRecipientProtector(
                    firstServices.GetRequiredService<IDataProtectionProvider>(),
                    v1Settings);
                protectedRecipient = firstProtector.Protect(
                    "0123456789",
                    "NGUYEN VAN A");
            }

            Assert.Equal("v1", protectedRecipient.EncryptionKeyVersion);
            Assert.DoesNotContain(
                "0123456789",
                Encoding.UTF8.GetString(protectedRecipient.AccountNumberCiphertext),
                StringComparison.Ordinal);

            using (var restartedServices = CreateRefundDataProtectionServices(keyDirectory))
            {
                var restartedProtector = new RefundRecipientProtector(
                    restartedServices.GetRequiredService<IDataProtectionProvider>(),
                    v1Settings);
                var unprotected = restartedProtector.Unprotect(
                    protectedRecipient.AccountNumberCiphertext,
                    protectedRecipient.AccountHolderCiphertext,
                    protectedRecipient.EncryptionKeyVersion);
                Assert.Equal("0123456789", unprotected.AccountNumber);
                Assert.Equal("NGUYEN VAN A", unprotected.AccountHolder);
            }

            Guid masterKeyB;
            using (var rotationServices = CreateRefundDataProtectionServices(keyDirectory))
            {
                masterKeyB = rotationServices.GetRequiredService<IKeyManager>()
                    .CreateNewKey(now.AddMinutes(-1), now.AddDays(91)).KeyId;
            }
            Assert.NotEqual(masterKeyA, masterKeyB);

            ProtectedRefundRecipient protectedAfterRotation;
            using (var rotatedServices = CreateRefundDataProtectionServices(keyDirectory))
            {
                var rotatedProtector = new RefundRecipientProtector(
                    rotatedServices.GetRequiredService<IDataProtectionProvider>(),
                    v2Settings);
                protectedAfterRotation = rotatedProtector.Protect(
                    "9988776655",
                    "TRAN VAN B");
                Assert.Equal("v2", protectedAfterRotation.EncryptionKeyVersion);
            }

            var masterKeyBFile = Path.Combine(
                keyDirectory.FullName,
                $"key-{masterKeyB:D}.xml");
            Assert.True(File.Exists(masterKeyBFile));
            File.Copy(
                masterKeyBFile,
                Path.Combine(keyBOnlyDirectory.FullName, Path.GetFileName(masterKeyBFile)));
            using (var activeKeyOnlyServices =
                   CreateRefundDataProtectionServices(keyBOnlyDirectory))
            {
                var activeKeyOnlyProtector = new RefundRecipientProtector(
                    activeKeyOnlyServices.GetRequiredService<IDataProtectionProvider>(),
                    v2Settings);
                Assert.Equal(
                    "9988776655",
                    activeKeyOnlyProtector.Unprotect(
                        protectedAfterRotation.AccountNumberCiphertext,
                        protectedAfterRotation.AccountHolderCiphertext,
                        "v2").AccountNumber);
                Assert.Throws<CryptographicException>(() =>
                    new RefundRecipientProtector(
                            activeKeyOnlyServices.GetRequiredService<IDataProtectionProvider>(),
                            v1Settings)
                        .Unprotect(
                            protectedRecipient.AccountNumberCiphertext,
                            protectedRecipient.AccountHolderCiphertext,
                            "v1"));
            }

            using (var secondRestartServices = CreateRefundDataProtectionServices(keyDirectory))
            {
                var oldProtector = new RefundRecipientProtector(
                    secondRestartServices.GetRequiredService<IDataProtectionProvider>(),
                    v1Settings);
                var newProtector = new RefundRecipientProtector(
                    secondRestartServices.GetRequiredService<IDataProtectionProvider>(),
                    v2Settings);
                Assert.Equal(
                    "0123456789",
                    oldProtector.Unprotect(
                        protectedRecipient.AccountNumberCiphertext,
                        protectedRecipient.AccountHolderCiphertext,
                        "v1").AccountNumber);
                Assert.Equal(
                    "9988776655",
                    newProtector.Unprotect(
                        protectedAfterRotation.AccountNumberCiphertext,
                        protectedAfterRotation.AccountHolderCiphertext,
                        "v2").AccountNumber);
            }
        }
        finally
        {
            keyDirectory.Delete(recursive: true);
            keyBOnlyDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Refund_request_flow_enforces_auth_csrf_eligibility_idempotency_and_masking()
    {
        const string subject = "rf01-http-user";
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        factory.TokenEndpoint.SetProfile(subject, "RF01 User", null, null);
        using var anonymous = CreateClient(factory);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/v1/refunds/eligibility?paymentOrderId={Guid.NewGuid()}"))
            .StatusCode);

        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/refunds", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrf = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        Guid userId;
        Guid orderId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            userId = await context.GoogleIdentities
                .Where(identity => identity.GoogleSubject == subject)
                .Select(identity => identity.UserId)
                .SingleAsync();
            var paidAt = DateTimeOffset.UtcNow.AddDays(-1);
            orderId = Guid.NewGuid();
            var order = PaymentOrder.CreateTopUp(
                orderId,
                userId,
                $"ZENO-{orderId:N}",
                100_000,
                10_000,
                "rf01-seed",
                new string('a', 64),
                paidAt.AddMinutes(-10),
                paidAt.AddMinutes(10));
            order.FulfillTopUp(paidAt);
            context.PaymentOrders.Add(order);
            context.PaidCoinLots.Add(PaidCoinLot.Create(
                Guid.NewGuid(), userId, orderId, 10_000, paidAt));
            await context.SaveChangesAsync();
        }

        var eligibilityResponse = await client.GetAsync(
            $"/api/v1/refunds/eligibility?paymentOrderId={orderId}");
        Assert.Equal(HttpStatusCode.OK, eligibilityResponse.StatusCode);
        var eligibility = await eligibilityResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal(5_000, eligibility.GetProperty("feeVnd").GetInt64());
        Assert.Equal(95_000, eligibility.GetProperty("netRefundVnd").GetInt64());

        using var missingCsrf = RefundRequestMessage(orderId, "rf01-http");
        await AssertStandardizedErrorAsync(
            await client.SendAsync(missingCsrf),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        using var createRequest = RefundRequestMessage(orderId, "rf01-http", csrf);
        var createdResponse = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var refundId = created.GetProperty("refundId").GetGuid();
        Assert.Equal("Requested", created.GetProperty("status").GetString());

        using var replayRequest = RefundRequestMessage(orderId, "rf01-http", csrf);
        var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(refundId, replay.GetProperty("refundId").GetGuid());

        using var secondKeyRequest = RefundRequestMessage(orderId, "rf01-other-key", csrf);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(secondKeyRequest),
            HttpStatusCode.Conflict,
            "REFUND_ALREADY_EXISTS");

        var detailResponse = await client.GetAsync($"/api/v1/refunds/{refundId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        var recipient = detail.GetProperty("recipient");
        Assert.Equal("******6789", recipient.GetProperty("accountNumberMasked").GetString());
        Assert.Equal("N*** V*** A***", recipient.GetProperty("accountHolderMasked").GetString());
        Assert.DoesNotContain("\"accountNumber\"", detail.GetRawText(), StringComparison.Ordinal);

        var listResponse = await client.GetAsync("/api/v1/refunds");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(list.EnumerateArray());

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedRecipient = await verification.RefundRecipients.AsNoTracking().SingleAsync();
        Assert.Equal("v1", persistedRecipient.EncryptionKeyVersion);
        var protector = factory.Services.GetRequiredService<IRefundRecipientProtector>();
        var plaintextRecipient = protector.Unprotect(
            persistedRecipient.AccountNumberEncrypted,
            persistedRecipient.AccountHolderEncrypted,
            persistedRecipient.EncryptionKeyVersion);
        Assert.Equal("0123456789", plaintextRecipient.AccountNumber);
        Assert.Equal("NGUYEN VAN A", plaintextRecipient.AccountHolder);
        Assert.DoesNotContain(
            "0123456789",
            Encoding.UTF8.GetString(persistedRecipient.AccountNumberEncrypted),
            StringComparison.Ordinal);
        Assert.Empty(await verification.WalletEntries.ToListAsync());
    }

    [Fact]
    public async Task Refund_create_is_limited_to_ten_requests_per_minute_per_user()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        factory.TokenEndpoint.SetProfile("rf01-rate-user", "RF01 Rate User", null, null);
        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/refunds", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrf = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var request = RefundRequestMessage(
                Guid.NewGuid(), $"rf01-rate-{attempt}", csrf);
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        }

        using var rejected = RefundRequestMessage(Guid.NewGuid(), "rf01-rate-11", csrf);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(rejected),
            HttpStatusCode.TooManyRequests,
            "RATE_LIMITED");
    }

    [Fact]
    public async Task Refund_create_rejects_invalid_keys_missing_fields_and_unexpected_payload()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        factory.TokenEndpoint.SetProfile("rf01-validation-user", "RF01 Validation", null, null);
        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/refunds", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrf = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        async Task AssertInvalidAsync(HttpRequestMessage request)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            await AssertStandardizedErrorAsync(
                await client.SendAsync(request),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }

        var orderId = Guid.NewGuid();
        using (var missing = RefundRequestMessage(orderId, "missing-key", csrf))
        {
            missing.Headers.Remove("Idempotency-Key");
            await AssertStandardizedErrorAsync(
                await client.SendAsync(missing),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }

        using (var whitespace = RefundRequestMessage(orderId, " "))
        {
            await AssertInvalidAsync(whitespace);
        }

        using (var tooLong = RefundRequestMessage(
                   orderId,
                   new string('k', RefundRequest.MaximumIdempotencyKeyLength + 1)))
        {
            await AssertInvalidAsync(tooLong);
        }

        using (var missingFields = new HttpRequestMessage(HttpMethod.Post, "/api/v1/refunds")
               {
                   Content = JsonContent.Create(new { paymentOrderId = orderId }),
               })
        {
            missingFields.Headers.Add("Idempotency-Key", "missing-fields");
            await AssertInvalidAsync(missingFields);
        }

        using (var unexpected = new HttpRequestMessage(HttpMethod.Post, "/api/v1/refunds")
               {
                   Content = JsonContent.Create(new
                   {
                       paymentOrderId = orderId,
                       reason = "duplicate transfer",
                       originalAmountVnd = 1,
                       recipient = new
                       {
                           bankCode = "VCB",
                           accountNumber = "0123456789",
                           accountHolder = "NGUYEN VAN A",
                       },
                   }),
               })
        {
            unexpected.Headers.Add("Idempotency-Key", "unexpected-field");
            await AssertInvalidAsync(unexpected);
        }

        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/refunds")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        malformed.Headers.Add("Idempotency-Key", "malformed-body");
        await AssertInvalidAsync(malformed);
    }

    [Fact]
    public async Task Refund_http_fails_closed_for_expiry_lot_drift_order_type_and_cross_user_reads()
    {
        const string ownerSubject = "rf01-owner";
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory);
        factory.TokenEndpoint.SetProfile(ownerSubject, "RF01 Owner", null, null);
        using var owner = CreateClient(factory);
        await CompleteLoginAsync(
            owner,
            await BeginLoginAsync(owner, "/refunds", factory.TokenEndpoint));
        var ownerProfile = await owner.GetAsync("/api/v1/me");
        var ownerCsrf = Assert.Single(ownerProfile.Headers.GetValues("X-CSRF-TOKEN"));

        Guid ownerId;
        var expiredOrderId = Guid.NewGuid();
        var spentOrderId = Guid.NewGuid();
        var refundedOrderId = Guid.NewGuid();
        var reservedOrderId = Guid.NewGuid();
        var validOrderId = Guid.NewGuid();
        var vipOrderId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ownerId = await context.GoogleIdentities
                .Where(identity => identity.GoogleSubject == ownerSubject)
                .Select(identity => identity.UserId)
                .SingleAsync();

            void AddTopUp(Guid orderId, DateTimeOffset paidAt, Action<PaidCoinLot>? mutate = null)
            {
                var order = PaymentOrder.CreateTopUp(
                    orderId,
                    ownerId,
                    $"ZENO-{orderId:N}",
                    100_000,
                    10_000,
                    $"rf01-http-{orderId:N}",
                    new string('a', 64),
                    paidAt.AddMinutes(-10),
                    paidAt.AddMinutes(10));
                order.FulfillTopUp(paidAt);
                var lot = PaidCoinLot.Create(Guid.NewGuid(), ownerId, orderId, 10_000, paidAt);
                mutate?.Invoke(lot);
                context.PaymentOrders.Add(order);
                context.PaidCoinLots.Add(lot);
            }

            AddTopUp(expiredOrderId, DateTimeOffset.UtcNow.AddDays(-8));
            AddTopUp(spentOrderId, DateTimeOffset.UtcNow.AddDays(-1), lot => lot.Spend(1));
            AddTopUp(refundedOrderId, DateTimeOffset.UtcNow.AddDays(-1), lot =>
            {
                lot.Reserve(1);
                lot.RefundReserved(1);
            });
            AddTopUp(reservedOrderId, DateTimeOffset.UtcNow.AddDays(-1), lot => lot.Reserve(1));
            AddTopUp(validOrderId, DateTimeOffset.UtcNow.AddDays(-1));
            context.PaymentOrders.Add(PaymentOrder.CreateVipPurchase(
                vipOrderId,
                ownerId,
                $"ZENO-{vipOrderId:N}",
                VipPlan.InitialPriceVnd,
                VipPlan.InitialPlanId,
                VipPlan.FixedDurationHours,
                VipPlan.FixedDiscountPercent,
                "rf01-vip-order",
                new string('b', 64),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(30)));
            await context.SaveChangesAsync();
        }

        async Task AssertCreateErrorAsync(Guid orderId, string key, string errorCode)
        {
            using var request = RefundRequestMessage(orderId, key, ownerCsrf);
            await AssertStandardizedErrorAsync(
                await owner.SendAsync(request),
                HttpStatusCode.UnprocessableEntity,
                errorCode);
        }

        await AssertCreateErrorAsync(expiredOrderId, "expired", "REFUND_WINDOW_EXPIRED");
        await AssertCreateErrorAsync(spentOrderId, "spent", "REFUND_RECOVERY_INSUFFICIENT");
        await AssertCreateErrorAsync(refundedOrderId, "refunded", "REFUND_RECOVERY_INSUFFICIENT");
        await AssertCreateErrorAsync(reservedOrderId, "reserved", "REFUND_RECOVERY_INSUFFICIENT");
        await AssertCreateErrorAsync(vipOrderId, "vip", "REFUND_ORDER_NOT_ELIGIBLE");

        using var create = RefundRequestMessage(validOrderId, "owned-refund", ownerCsrf);
        var createdResponse = await owner.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var refundId = (await createdResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("refundId").GetGuid();

        factory.TokenEndpoint.SetProfile("rf01-other-user", "RF01 Other", null, null);
        using var other = CreateClient(factory);
        await CompleteLoginAsync(
            other,
            await BeginLoginAsync(other, "/refunds", factory.TokenEndpoint));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await other.GetAsync($"/api/v1/refunds/{refundId}")) .StatusCode);
        var otherList = await other.GetFromJsonAsync<JsonElement>("/api/v1/refunds");
        Assert.Empty(otherList.EnumerateArray());
    }

    [Fact]
    public async Task Refund_create_returns_forbidden_for_authenticated_principal_without_user_role()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            useInsufficientRoleTestAuthentication: true);
        using var client = CreateClient(factory);
        using var request = RefundRequestMessage(Guid.NewGuid(), "rf01-forbidden");

        await AssertStandardizedErrorAsync(
            await client.SendAsync(request),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");
    }

    [Fact]
    public async Task Refund_openapi_publishes_contract_and_standardized_errors()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        var create = paths.GetProperty("/api/v1/refunds").GetProperty("post");
        foreach (var status in new[] { "201", "400", "401", "403", "404", "409", "422", "429" })
        {
            Assert.True(create.GetProperty("responses").TryGetProperty(status, out _), status);
        }

        var createSchema = ResolveResponseSchema(document, create, "201");
        foreach (var property in new[]
                 {
                     "refundId", "status", "originalAmountVnd", "feeVnd",
                     "netRefundVnd", "requestedAt",
                 })
        {
            Assert.True(createSchema.GetProperty("properties").TryGetProperty(property, out _), property);
        }
        Assert.Equal(
            ["Requested"],
            GetOpenApiEnumValues(
                document,
                createSchema.GetProperty("properties").GetProperty("status")));

        var idempotencyHeader = FindOpenApiParameter(create, "Idempotency-Key");
        Assert.Equal("header", idempotencyHeader.GetProperty("in").GetString());
        Assert.True(idempotencyHeader.GetProperty("required").GetBoolean());
        Assert.Equal(1, idempotencyHeader.GetProperty("schema").GetProperty("minLength").GetInt32());
        Assert.Equal(
            RefundRequest.MaximumIdempotencyKeyLength,
            idempotencyHeader.GetProperty("schema").GetProperty("maxLength").GetInt32());

        var requestSchema = ResolveOpenApiSchema(
            document,
            create.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        var requiredRequestFields = requestSchema.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("paymentOrderId", requiredRequestFields);
        Assert.Contains("reason", requiredRequestFields);
        Assert.Contains("recipient", requiredRequestFields);
        var reasonSchema = ResolveOpenApiSchema(
            document,
            requestSchema.GetProperty("properties").GetProperty("reason"));
        Assert.Equal(1, reasonSchema.GetProperty("minLength").GetInt32());
        Assert.Equal(
            RefundRequest.MaximumReasonLength,
            reasonSchema.GetProperty("maxLength").GetInt32());
        var recipientSchema = ResolveOpenApiSchema(
            document,
            requestSchema.GetProperty("properties").GetProperty("recipient"));
        Assert.True(
            recipientSchema.TryGetProperty("required", out var recipientRequired),
            recipientSchema.GetRawText());
        var requiredRecipientFields = recipientRequired
            .EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.True(requiredRecipientFields.SetEquals(
            ["bankCode", "accountNumber", "accountHolder"]));
        var recipientProperties = recipientSchema.GetProperty("properties");
        AssertStringLengthContract(
            ResolveOpenApiSchema(document, recipientProperties.GetProperty("bankCode")),
            2,
            RefundRecipient.MaximumBankCodeLength);
        AssertStringLengthContract(
            ResolveOpenApiSchema(document, recipientProperties.GetProperty("accountNumber")),
            4,
            RefundRecipient.MaximumAccountNumberLength);
        AssertStringLengthContract(
            ResolveOpenApiSchema(document, recipientProperties.GetProperty("accountHolder")),
            2,
            RefundRecipient.MaximumAccountHolderLength);

        foreach (var status in new[] { "400", "401", "403", "404", "409", "422", "429" })
        {
            var errorSchema = ResolveResponseSchema(document, create, status);
            foreach (var property in new[] { "errorCode", "message", "traceId" })
            {
                Assert.True(errorSchema.GetProperty("properties").TryGetProperty(property, out _),
                    $"{status}:{property}");
            }
        }

        var eligibility = paths.GetProperty("/api/v1/refunds/eligibility").GetProperty("get");
        Assert.Equal(
            "paymentOrderId",
            FindOpenApiParameter(eligibility, "paymentOrderId")
                .GetProperty("name")
                .GetString());
        Assert.True(paths.TryGetProperty("/api/v1/refunds/{refundId}", out _));
        var eligibilitySchema = ResolveResponseSchema(document, eligibility, "200");
        foreach (var property in new[]
                 {
                     "orderId", "originalAmountVnd", "feeVnd", "netRefundVnd",
                     "lotGrantedCoins", "lotAvailableCoins", "eligible", "reasonCode", "paidAt",
                 })
        {
            Assert.True(eligibilitySchema.GetProperty("properties").TryGetProperty(property, out _),
                property);
        }

        var detail = paths.GetProperty("/api/v1/refunds/{refundId}").GetProperty("get");
        var detailSchema = ResolveResponseSchema(document, detail, "200");
        Assert.Equal(
            Enum.GetNames<RefundStatus>(),
            GetOpenApiEnumValues(
                document,
                detailSchema.GetProperty("properties").GetProperty("status")));
        var list = paths.GetProperty("/api/v1/refunds").GetProperty("get");
        var listSchema = ResolveResponseSchema(document, list, "200");
        Assert.Equal("array", listSchema.GetProperty("type").GetString());
        Assert.True(ResolveOpenApiSchema(document, listSchema.GetProperty("items"))
            .GetProperty("properties").TryGetProperty("recipient", out _));

        foreach (var route in new[]
                 {
                     "review", "approve", "reject", "transfer-attempts", "complete", "release",
                 })
        {
            var operation = paths
                .GetProperty($"/api/v1/admin/refunds/{{refundId}}/{route}")
                .GetProperty("post");
            foreach (var status in route is "approve" or "complete" or "release"
                         ? new[] { "200", "400", "401", "403", "404", "409", "422", "429" }
                         : new[] { "200", "400", "401", "403", "404", "409", "429" })
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _),
                    $"{route}:{status}");

                if (status != "200")
                {
                    var errorSchema = ResolveResponseSchema(document, operation, status);
                    foreach (var property in new[] { "errorCode", "message", "traceId" })
                    {
                        Assert.True(
                            errorSchema.GetProperty("properties").TryGetProperty(property, out _),
                            $"{route}:{status}:{property}");
                    }
                }
            }

            var adminSchema = ResolveResponseSchema(document, operation, "200");
            foreach (var property in new[]
                     {
                         "refundId", "status", "requiredRecoveryCoins", "version",
                         "reviewedBy", "reviewedAt", "approvedBy", "approvedAt",
                         "decisionReason", "reservationId", "walletOperationId",
                         "transferRecordId", "transferStatus", "resolutionOperationId",
                     })
            {
                Assert.True(adminSchema.GetProperty("properties").TryGetProperty(property, out _),
                    $"{route}:{property}");
            }

            if (route is "approve" or "transfer-attempts" or "complete" or "release")
            {
                var approvalKey = FindOpenApiParameter(operation, "Idempotency-Key");
                Assert.True(approvalKey.GetProperty("required").GetBoolean());
                Assert.Equal(1,
                    approvalKey.GetProperty("schema").GetProperty("minLength").GetInt32());
                Assert.Equal(200,
                    approvalKey.GetProperty("schema").GetProperty("maxLength").GetInt32());
            }
        }

        var transferOperation = paths
            .GetProperty("/api/v1/admin/refunds/{refundId}/transfer-attempts")
            .GetProperty("post");
        var transferRequestSchema = ResolveOpenApiSchema(document,
            transferOperation.GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema"));
        var transferRequired = transferRequestSchema.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("reportedOutcome", transferRequired);
        Assert.Equal(["Succeeded", "Failed", "Unknown"], GetOpenApiEnumValues(document,
            transferRequestSchema.GetProperty("properties").GetProperty("reportedOutcome")));
    }

    [Fact]
    public async Task Admin_refund_mutations_are_limited_to_twenty_requests_per_minute_per_admin()
    {
        var (admin, csrf) = await CreateAdminClientAsync("rf02-rate-limit-admin");
        var (otherAdmin, otherCsrf) = await CreateAdminClientAsync(
            "rf02-rate-limit-other-admin");
        using (admin)
        using (otherAdmin)
        {
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/v1/admin/refunds/{Guid.NewGuid()}/review");
                request.Headers.Add("X-CSRF-TOKEN", csrf);

                await AssertStandardizedErrorAsync(
                    await admin.SendAsync(request),
                    HttpStatusCode.NotFound,
                    "RESOURCE_NOT_FOUND");
            }

            using var limitedRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/admin/refunds/{Guid.NewGuid()}/review");
            limitedRequest.Headers.Add("X-CSRF-TOKEN", csrf);

            await AssertStandardizedErrorAsync(
                await admin.SendAsync(limitedRequest),
                HttpStatusCode.TooManyRequests,
                "RATE_LIMITED");

            using var independentRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/admin/refunds/{Guid.NewGuid()}/review");
            independentRequest.Headers.Add("X-CSRF-TOKEN", otherCsrf);
            await AssertStandardizedErrorAsync(
                await otherAdmin.SendAsync(independentRequest),
                HttpStatusCode.NotFound,
                "RESOURCE_NOT_FOUND");
        }
    }

    [Fact]
    public async Task Admin_refund_review_and_approval_reserve_original_lot_through_http()
    {
        var (admin, csrf) = await CreateAdminClientAsync("rf02-admin-approval");
        using (admin)
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var ownerId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var refundId = Guid.NewGuid();
            var lotId = Guid.NewGuid();
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var owner = User.CreateFromGoogle(
                    ownerId, "RF02 Refund Owner", null, null, now.AddDays(-10));
                var order = PaymentOrder.CreateTopUp(
                    orderId,
                    ownerId,
                    $"ZENO-{orderId:N}",
                    100_000,
                    10_000,
                    $"rf02-order-{orderId:N}",
                    new string('a', 64),
                    now.AddMinutes(-10),
                    now.AddMinutes(10));
                order.FulfillTopUp(now);
                var lot = PaidCoinLot.Create(lotId, ownerId, orderId, 10_000, now);
                var wallet = WalletAccount.Create(ownerId, now);
                wallet.CreditPaid(10_000, now);
                var credit = WalletOperation.Create(
                    Guid.NewGuid(), ownerId, WalletOperationTypes.TopUpCredit,
                    WalletOperationSourceTypes.PaymentOrder, orderId, null, now);
                var refund = RefundRequest.Create(
                    refundId,
                    orderId,
                    ownerId,
                    "Duplicate transfer",
                    100_000,
                    10_000,
                    "rf02-user-key",
                    new string('b', 64),
                    "v1",
                    now);
                var protectedRecipient = _factory.Services
                    .GetRequiredService<IRefundRecipientProtector>()
                    .Protect("0123456789", "NGUYEN VAN A");
                var recipient = RefundRecipient.Create(
                    Guid.NewGuid(), refundId, "VCB",
                    protectedRecipient.AccountNumberCiphertext,
                    protectedRecipient.AccountHolderCiphertext,
                    "6789", "******6789", "N*** V*** A***",
                    protectedRecipient.EncryptionKeyVersion, now);
                context.Users.Add(owner);
                context.PaymentOrders.Add(order);
                context.PaidCoinLots.Add(lot);
                context.Wallets.Add(wallet);
                context.WalletOperations.Add(credit);
                context.WalletEntries.Add(WalletEntry.Create(
                    Guid.NewGuid(), credit, WalletCoinTypes.Paid,
                    WalletBuckets.Available, 10_000, 1, now));
                context.RefundRequests.Add(refund);
                context.RefundRecipients.Add(recipient);
                await context.SaveChangesAsync();
            }

            using (var anonymous = CreateClient())
            {
                Assert.Equal(
                    HttpStatusCode.Unauthorized,
                    (await anonymous.PostAsync(
                        $"/api/v1/admin/refunds/{refundId}/review", null)).StatusCode);
            }

            _factory.TokenEndpoint.SetProfile(
                $"rf02-user-role-{Guid.NewGuid():N}", "RF02 User", null, null);
            using (var user = CreateClient())
            {
                await CompleteLoginAsync(user, await BeginLoginAsync(user, "/refunds"));
                var userProfile = await user.GetAsync("/api/v1/me");
                var userCsrf = Assert.Single(userProfile.Headers.GetValues("X-CSRF-TOKEN"));
                using var forbidden = new HttpRequestMessage(
                    HttpMethod.Post, $"/api/v1/admin/refunds/{refundId}/review");
                forbidden.Headers.Add("X-CSRF-TOKEN", userCsrf);
                await AssertStandardizedErrorAsync(
                    await user.SendAsync(forbidden),
                    HttpStatusCode.Forbidden,
                    "FORBIDDEN");
            }

            var missingCsrf = await admin.PostAsync(
                $"/api/v1/admin/refunds/{refundId}/review", null);
            await AssertStandardizedErrorAsync(
                missingCsrf,
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");

            using var reviewRequest = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/admin/refunds/{refundId}/review");
            reviewRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            var reviewResponse = await admin.SendAsync(reviewRequest);
            Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
            var reviewed = await reviewResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("UnderReview", reviewed.GetProperty("status").GetString());
            var reviewedVersion = reviewed.GetProperty("version").GetInt64();

            using (var malformed = new HttpRequestMessage(
                       HttpMethod.Post, $"/api/v1/admin/refunds/{refundId}/approve")
                   {
                       Content = JsonContent.Create(new
                       {
                           expectedVersion = reviewedVersion,
                           decisionNote = "Verified",
                           paidCoins = 1,
                       }),
                   })
            {
                malformed.Headers.Add("X-CSRF-TOKEN", csrf);
                malformed.Headers.Add("Idempotency-Key", "rf02-malformed");
                await AssertStandardizedErrorAsync(
                    await admin.SendAsync(malformed),
                    HttpStatusCode.BadRequest,
                    "VALIDATION_FAILED");
            }

            using (var missingKey = new HttpRequestMessage(
                       HttpMethod.Post, $"/api/v1/admin/refunds/{refundId}/approve")
                   {
                       Content = JsonContent.Create(new
                       {
                           expectedVersion = reviewedVersion,
                           decisionNote = "Verified",
                       }),
                   })
            {
                missingKey.Headers.Add("X-CSRF-TOKEN", csrf);
                await AssertStandardizedErrorAsync(
                    await admin.SendAsync(missingKey),
                    HttpStatusCode.BadRequest,
                    "VALIDATION_FAILED");
            }

            using (var staleApproval = new HttpRequestMessage(
                       HttpMethod.Post, $"/api/v1/admin/refunds/{refundId}/approve")
                   {
                       Content = JsonContent.Create(new
                       {
                           expectedVersion = reviewedVersion - 1,
                           decisionNote = "Original lot fully verified",
                       }),
                   })
            {
                staleApproval.Headers.Add("X-CSRF-TOKEN", csrf);
                staleApproval.Headers.Add("Idempotency-Key", "rf02-http-stale-approval");
                await AssertStandardizedErrorAsync(
                    await admin.SendAsync(staleApproval),
                    HttpStatusCode.Conflict,
                    "CONCURRENCY_CONFLICT");
            }

            using var approveRequest = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/admin/refunds/{refundId}/approve")
            {
                Content = JsonContent.Create(new
                {
                    expectedVersion = reviewedVersion,
                    decisionNote = "Original lot fully verified",
                }),
            };
            approveRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            approveRequest.Headers.Add("Idempotency-Key", "rf02-http-approval");
            var approveResponse = await admin.SendAsync(approveRequest);
            Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
            var approved = await approveResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("RecoveryReserved", approved.GetProperty("status").GetString());
            Assert.Equal(10_000, approved.GetProperty("requiredRecoveryCoins").GetInt64());
            Assert.NotEqual(Guid.Empty, approved.GetProperty("reservationId").GetGuid());
            Assert.NotEqual(Guid.Empty, approved.GetProperty("walletOperationId").GetGuid());

            using var replayRequest = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/admin/refunds/{refundId}/approve")
            {
                Content = JsonContent.Create(new
                {
                    expectedVersion = reviewedVersion,
                    decisionNote = "Original lot fully verified",
                }),
            };
            replayRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            replayRequest.Headers.Add("Idempotency-Key", "rf02-http-approval");
            Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(replayRequest)).StatusCode);

            using var conflictRequest = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/admin/refunds/{refundId}/approve")
            {
                Content = JsonContent.Create(new
                {
                    expectedVersion = reviewedVersion,
                    decisionNote = "Different semantic request",
                }),
            };
            conflictRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            conflictRequest.Headers.Add("Idempotency-Key", "rf02-http-approval");
            await AssertStandardizedErrorAsync(
                await admin.SendAsync(conflictRequest),
                HttpStatusCode.Conflict,
                "IDEMPOTENCY_CONFLICT");

            await using var verificationScope = _factory.Services.CreateAsyncScope();
            var verification = verificationScope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            Assert.Equal(1, await verification.CoinReservations.CountAsync(
                reservation => reservation.RefundRequestId == refundId));
            var persistedWallet = await verification.Wallets.AsNoTracking()
                .SingleAsync(candidate => candidate.UserId == ownerId);
            Assert.Equal(0, persistedWallet.PaidAvailable);
            Assert.Equal(10_000, persistedWallet.PaidReserved);
            Assert.Equal(1, await verification.OutboxMessages.CountAsync(message =>
                message.EventType == BillingEventTypes.RefundRecoveryReserved));

            var approvedVersion = approved.GetProperty("version").GetInt64();
            var (payoutAdmin, payoutCsrf) = await CreateAdminClientAsync(
                "rf03-payout-validation-admin");
            using var payoutAdminScope = payoutAdmin;
            string TransferBody(string reportedOutcomeJson, bool unexpected = false) => $$"""
                {
                  "expectedVersion": {{approvedVersion}},
                  "bankReference": "bank-validation-{{refundId:N}}",
                  "evidenceReference": "evidence://manual-bank-proof",
                  "reportedOutcome": {{reportedOutcomeJson}}{{(unexpected ? ",\"amountVnd\":1" : string.Empty)}}
                }
                """;

            async Task AssertInvalidTransferAsync(string body, string? key = "rf03-validation-key",
                string? csrfToken = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/v1/admin/refunds/{refundId}/transfer-attempts")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-CSRF-TOKEN", csrfToken ?? payoutCsrf);
                if (key is not null)
                {
                    request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
                }

                await AssertStandardizedErrorAsync(await payoutAdmin.SendAsync(request),
                    HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            }

            await AssertInvalidTransferAsync($$"""
                {"expectedVersion":{{approvedVersion}},"bankReference":"bank-missing-outcome",
                 "evidenceReference":"evidence://manual-bank-proof"}
                """);
            await AssertInvalidTransferAsync(TransferBody("null"));
            await AssertInvalidTransferAsync(TransferBody("1"));
            await AssertInvalidTransferAsync(TransferBody("\"NotAnOutcome\""));
            await AssertInvalidTransferAsync(TransferBody("\"Succeeded\"", unexpected: true));
            await AssertInvalidTransferAsync(TransferBody("\"Succeeded\""), key: null);
            await AssertInvalidTransferAsync(TransferBody("\"Succeeded\""), key: " ");
            await AssertInvalidTransferAsync(TransferBody("\"Succeeded\""), key: new string('k', 201));
            await AssertInvalidTransferAsync(TransferBody("\"Succeeded\""),
                key: "rf03-invalid-csrf", csrfToken: "invalid-csrf-token");
            using (var missingCsrfTransfer = new HttpRequestMessage(HttpMethod.Post,
                       $"/api/v1/admin/refunds/{refundId}/transfer-attempts")
                   {
                       Content = new StringContent(TransferBody("\"Succeeded\""),
                           Encoding.UTF8, "application/json"),
                   })
            {
                missingCsrfTransfer.Headers.Add("Idempotency-Key", "rf03-missing-csrf");
                await AssertStandardizedErrorAsync(await payoutAdmin.SendAsync(missingCsrfTransfer),
                    HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            }

            using (var anonymous = CreateClient())
            using (var anonymousTransfer = new HttpRequestMessage(HttpMethod.Post,
                       $"/api/v1/admin/refunds/{refundId}/transfer-attempts")
                   {
                       Content = new StringContent(TransferBody("\"Succeeded\""),
                           Encoding.UTF8, "application/json"),
                   })
            {
                anonymousTransfer.Headers.Add("Idempotency-Key", "rf03-anonymous-transfer");
                Assert.Equal(HttpStatusCode.Unauthorized,
                    (await anonymous.SendAsync(anonymousTransfer)).StatusCode);
            }

            _factory.TokenEndpoint.SetProfile(
                $"rf03-user-role-{Guid.NewGuid():N}", "RF03 User", null, null);
            using (var user = CreateClient())
            {
                await CompleteLoginAsync(user, await BeginLoginAsync(user, "/refunds"));
                var userProfile = await user.GetAsync("/api/v1/me");
                var userCsrf = Assert.Single(userProfile.Headers.GetValues("X-CSRF-TOKEN"));
                using var forbidden = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/v1/admin/refunds/{refundId}/transfer-attempts")
                {
                    Content = new StringContent(TransferBody("\"Succeeded\""),
                        Encoding.UTF8, "application/json"),
                };
                forbidden.Headers.Add("X-CSRF-TOKEN", userCsrf);
                forbidden.Headers.Add("Idempotency-Key", "rf03-user-transfer");
                await AssertStandardizedErrorAsync(await user.SendAsync(forbidden),
                    HttpStatusCode.Forbidden, "FORBIDDEN");
            }

            verification.ChangeTracker.Clear();
            Assert.Equal(0, await verification.RefundTransferRecords.CountAsync(item =>
                item.RefundRequestId == refundId));
            var stillReserved = await verification.RefundRequests.AsNoTracking()
                .SingleAsync(item => item.Id == refundId);
            Assert.Equal(RefundRequestStatuses.RecoveryReserved, stillReserved.Status);

            using var transferRequest = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/admin/refunds/{refundId}/transfer-attempts")
            {
                Content = JsonContent.Create(new
                {
                    expectedVersion = approvedVersion,
                    bankReference = $"bank-{refundId:N}",
                    evidenceReference = "evidence://manual-bank-proof",
                    reportedOutcome = "Succeeded",
                }),
            };
            transferRequest.Headers.Add("X-CSRF-TOKEN", payoutCsrf);
            transferRequest.Headers.Add("Idempotency-Key", "rf03-http-transfer");
            var transferResponse = await payoutAdmin.SendAsync(transferRequest);
            Assert.True(transferResponse.StatusCode == HttpStatusCode.OK,
                await transferResponse.Content.ReadAsStringAsync());
            var transfer = await transferResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("TransferPending", transfer.GetProperty("status").GetString());
            Assert.Equal("ReportedSucceeded", transfer.GetProperty("transferStatus").GetString());

            using (var invalidRelease = new HttpRequestMessage(HttpMethod.Post,
                       $"/api/v1/admin/refunds/{refundId}/release")
                   {
                       Content = JsonContent.Create(new
                       {
                           expectedVersion = transfer.GetProperty("version").GetInt64(),
                           transferRecordId = transfer.GetProperty("transferRecordId").GetGuid(),
                           confirmationNote = "Cannot release a reported success",
                       }),
                   })
            {
                invalidRelease.Headers.Add("X-CSRF-TOKEN", payoutCsrf);
                invalidRelease.Headers.Add("Idempotency-Key", "rf03-invalid-release");
                await AssertStandardizedErrorAsync(await payoutAdmin.SendAsync(invalidRelease),
                    HttpStatusCode.UnprocessableEntity, "REFUND_TRANSFER_NOT_VERIFIED");
            }

            using var completeRequest = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/admin/refunds/{refundId}/complete")
            {
                Content = JsonContent.Create(new
                {
                    expectedVersion = transfer.GetProperty("version").GetInt64(),
                    transferRecordId = transfer.GetProperty("transferRecordId").GetGuid(),
                    confirmationNote = "Bank evidence independently reviewed",
                }),
            };
            completeRequest.Headers.Add("X-CSRF-TOKEN", payoutCsrf);
            completeRequest.Headers.Add("Idempotency-Key", "rf03-http-complete");
            var completeResponse = await payoutAdmin.SendAsync(completeRequest);
            Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
            var completed = await completeResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Completed", completed.GetProperty("status").GetString());

            verification.ChangeTracker.Clear();
            var completedWallet = await verification.Wallets.AsNoTracking()
                .SingleAsync(candidate => candidate.UserId == ownerId);
            var completedLot = await verification.PaidCoinLots.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == lotId);
            Assert.Equal(0, completedWallet.PaidReserved);
            Assert.Equal(10_000, completedLot.RefundedCoins);
            Assert.Equal(1, await verification.OutboxMessages.CountAsync(message =>
                message.EventType == BillingEventTypes.RefundCompleted));

            var rejectedOrderId = Guid.NewGuid();
            var rejectedRefundId = Guid.NewGuid();
            var rejectedOwnerId = Guid.NewGuid();
            var rejectedOrder = PaymentOrder.CreateTopUp(
                rejectedOrderId, rejectedOwnerId, $"ZENO-{rejectedOrderId:N}", 100_000, 10_000,
                $"rf02-reject-{rejectedOrderId:N}", new string('c', 64),
                now.AddMinutes(-10), now.AddMinutes(10));
            rejectedOrder.FulfillTopUp(now);
            var rejectedRefund = RefundRequest.Create(
                rejectedRefundId, rejectedOrderId, rejectedOwnerId, "Duplicate transfer",
                100_000, 10_000, "rf02-reject-key", new string('d', 64), "v1", now);
            rejectedRefund.BeginReview(rejectedOwnerId, now.AddSeconds(1));
            var rejectedProtectedRecipient = _factory.Services
                .GetRequiredService<IRefundRecipientProtector>()
                .Protect("0123456789", "NGUYEN VAN A");
            verification.Users.Add(User.CreateFromGoogle(
                rejectedOwnerId, "RF02 Rejected Owner", null, null, now.AddDays(-10)));
            verification.PaymentOrders.Add(rejectedOrder);
            verification.PaidCoinLots.Add(PaidCoinLot.Create(
                Guid.NewGuid(), rejectedOwnerId, rejectedOrderId, 10_000, now));
            verification.RefundRequests.Add(rejectedRefund);
            verification.RefundRecipients.Add(RefundRecipient.Create(
                Guid.NewGuid(), rejectedRefundId, "VCB",
                rejectedProtectedRecipient.AccountNumberCiphertext,
                rejectedProtectedRecipient.AccountHolderCiphertext,
                "6789", "******6789", "N*** V*** A***",
                rejectedProtectedRecipient.EncryptionKeyVersion, now));
            await verification.SaveChangesAsync();

            using (var staleReject = new HttpRequestMessage(
                       HttpMethod.Post, $"/api/v1/admin/refunds/{rejectedRefundId}/reject")
                   {
                       Content = JsonContent.Create(new
                       {
                           expectedVersion = 1,
                           reason = "Evidence mismatch",
                       }),
                   })
            {
                staleReject.Headers.Add("X-CSRF-TOKEN", csrf);
                await AssertStandardizedErrorAsync(
                    await admin.SendAsync(staleReject),
                    HttpStatusCode.Conflict,
                    "CONCURRENCY_CONFLICT");
            }

            using var reject = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/admin/refunds/{rejectedRefundId}/reject")
            {
                Content = JsonContent.Create(new
                {
                    expectedVersion = 2,
                    reason = "Evidence mismatch",
                }),
            };
            reject.Headers.Add("X-CSRF-TOKEN", csrf);
            var rejected = await admin.SendAsync(reject);
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            Assert.Equal("Rejected", (await rejected.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Admin_refund_transfer_outcomes_and_duplicate_evidence_follow_http_contract()
    {
        var (admin, csrf) = await CreateAdminClientAsync("rf03-transfer-contract-admin");
        using (admin)
        {
            async Task<JsonElement> RecordAsync(
                (Guid RefundId, long Version) seeded,
                string outcome,
                string bankReference,
                string key)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/v1/admin/refunds/{seeded.RefundId}/transfer-attempts")
                {
                    Content = JsonContent.Create(new
                    {
                        expectedVersion = seeded.Version,
                        bankReference,
                        evidenceReference = "evidence://rf03-http-contract",
                        reportedOutcome = outcome,
                    }),
                };
                request.Headers.Add("X-CSRF-TOKEN", csrf);
                request.Headers.Add("Idempotency-Key", key);
                var response = await admin.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.OK,
                    await response.Content.ReadAsStringAsync());
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            }

            async Task<HttpResponseMessage> ResolveAsync(
                Guid refundId, JsonElement transfer, bool complete, string key)
            {
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/v1/admin/refunds/{refundId}/{(complete ? "complete" : "release")}")
                {
                    Content = JsonContent.Create(new
                    {
                        expectedVersion = transfer.GetProperty("version").GetInt64(),
                        transferRecordId = transfer.GetProperty("transferRecordId").GetGuid(),
                        confirmationNote = "Evidence independently reconciled",
                    }),
                };
                request.Headers.Add("X-CSRF-TOKEN", csrf);
                request.Headers.Add("Idempotency-Key", key);
                return await admin.SendAsync(request);
            }

            var failed = await SeedRecoveryReservedRefundAsync("reported-failed");
            var failedTransfer = await RecordAsync(failed, "Failed",
                $"BANK-FAILED-{failed.RefundId:N}", "rf03-http-failed-transfer");
            await AssertStandardizedErrorAsync(
                await ResolveAsync(failed.RefundId, failedTransfer, true,
                    "rf03-http-failed-complete"),
                HttpStatusCode.UnprocessableEntity, "REFUND_TRANSFER_NOT_VERIFIED");
            Assert.Equal(HttpStatusCode.OK,
                (await ResolveAsync(failed.RefundId, failedTransfer, false,
                    "rf03-http-failed-release")).StatusCode);

            var unknownCompleted = await SeedRecoveryReservedRefundAsync("unknown-complete");
            var unknownCompleteTransfer = await RecordAsync(unknownCompleted, "Unknown",
                $"BANK-UNKNOWN-C-{unknownCompleted.RefundId:N}",
                "rf03-http-unknown-complete-transfer");
            Assert.Equal(HttpStatusCode.OK,
                (await ResolveAsync(unknownCompleted.RefundId, unknownCompleteTransfer, true,
                    "rf03-http-unknown-complete")).StatusCode);

            var unknownReleased = await SeedRecoveryReservedRefundAsync("unknown-release");
            var unknownReleaseTransfer = await RecordAsync(unknownReleased, "Unknown",
                $"BANK-UNKNOWN-R-{unknownReleased.RefundId:N}",
                "rf03-http-unknown-release-transfer");
            Assert.Equal(HttpStatusCode.OK,
                (await ResolveAsync(unknownReleased.RefundId, unknownReleaseTransfer, false,
                    "rf03-http-unknown-release")).StatusCode);

            const string duplicateBankReference = "RF03-HTTP-DUPLICATE-BANK";
            var duplicateWinner = await SeedRecoveryReservedRefundAsync("duplicate-winner");
            _ = await RecordAsync(duplicateWinner, "Succeeded", duplicateBankReference,
                "rf03-http-duplicate-winner");
            var duplicateLoser = await SeedRecoveryReservedRefundAsync("duplicate-loser");
            using var duplicateRequest = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/admin/refunds/{duplicateLoser.RefundId}/transfer-attempts")
            {
                Content = JsonContent.Create(new
                {
                    expectedVersion = duplicateLoser.Version,
                    bankReference = duplicateBankReference,
                    evidenceReference = "evidence://rf03-http-duplicate",
                    reportedOutcome = "Succeeded",
                }),
            };
            duplicateRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            duplicateRequest.Headers.Add("Idempotency-Key", "rf03-http-duplicate-loser");
            await AssertStandardizedErrorAsync(await admin.SendAsync(duplicateRequest),
                HttpStatusCode.Conflict, "TRANSFER_REFERENCE_CONFLICT");
        }
    }

    [Fact]
    public async Task Sepay_webhook_requires_valid_hmac_and_persists_normalized_evidence()
    {
        await using var factory = CreateSePayFactory();
        using var client = CreateClient(factory);
        var providerId = $"92{Random.Shared.Next(100_000, 999_999)}";

        using var request = SePayRequest(providerId, 100_000);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var acknowledgement = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(acknowledgement.GetProperty("success").GetBoolean());

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transaction = await context.ProviderTransactions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProviderTransactionId == providerId);
        Assert.Equal(
            ProviderTransactionProcessingStatuses.ReconciliationRequired,
            transaction.ProcessingStatus);
        Assert.Equal(
            SePayReconciliationReasons.OrderNotFound,
            transaction.ReconciliationReason);
        Assert.Equal(100_000, transaction.AmountVnd);
        Assert.Equal(SePayAccountNumber[^4..], transaction.AccountNumberLast4);
        var delivery = await context.WebhookDeliveries
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProviderTransactionId == transaction.Id);
        Assert.True(delivery.AuthVerified);
        Assert.Equal(
            WebhookDeliveryProcessingResults.ReconciliationRequired,
            delivery.ProcessingResult);
    }

    [Fact]
    public async Task Sepay_webhook_exact_topup_fulfills_real_wallet_transaction()
    {
        await using var factory = CreateSePayFactory();
        using var client = CreateClient(factory);
        var providerId = $"91{Random.Shared.Next(100_000, 999_999)}";
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderCode = $"ZENO-{orderId:N}";
        var now = DateTimeOffset.UtcNow;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Users.Add(User.CreateFromGoogle(
                userId,
                "B03 API User",
                null,
                null,
                now.AddMinutes(-1)));
            context.PaymentOrders.Add(PaymentOrder.CreateTopUp(
                orderId,
                userId,
                orderCode,
                100_000,
                10_000,
                $"api-{orderId:N}",
                new string('a', PaymentOrder.RequestFingerprintLength),
                now,
                now.AddMinutes(30)));
            await context.SaveChangesAsync();
        }

        using var request = SePayRequest(providerId, 100_000, paymentCode: orderCode);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var verification = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(TopUpOrderStatuses.Succeeded, (await verification.PaymentOrders
            .AsNoTracking()
            .SingleAsync(order => order.Id == orderId)).OrderStatus);
        Assert.Equal(10_000, (await verification.Wallets.AsNoTracking()
            .SingleAsync(wallet => wallet.UserId == userId)).PaidAvailable);
        Assert.Equal(1, await verification.PaidCoinLots.CountAsync(lot =>
            lot.TopUpOrderId == orderId));
        Assert.Equal(1, await verification.OutboxMessages.CountAsync(message =>
            message.EventType == BillingEventTypes.WalletTopUpCompleted));
    }

    [Fact]
    public async Task Fulfilled_topup_idempotency_replay_returns_succeeded_authoritative_state()
    {
        await using var factory = CreateSePayFactory();
        factory.TokenEndpoint.SetProfile(
            $"b03-replay-{Guid.NewGuid():N}",
            "B03 Replay User",
            null,
            null);
        using var client = CreateClient(factory);
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/wallet", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        using var createRequest = TopUpRequest("b03-succeeded-replay", 100_000, csrfToken);
        var createResponse = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = created.GetProperty("orderId").GetGuid();
        var orderCode = created.GetProperty("orderCode").GetString()!;
        using var webhook = SePayRequest(
            $"90{Random.Shared.Next(100_000, 999_999)}",
            100_000,
            paymentCode: orderCode);
        using var providerClient = CreateClient(factory);
        Assert.Equal(
            HttpStatusCode.OK,
            (await providerClient.SendAsync(webhook)).StatusCode);

        using var replayRequest = TopUpRequest("b03-succeeded-replay", 100_000, csrfToken);
        var replayResponse = await client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(orderId, replay.GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", replay.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Sepay_retry_is_acknowledged_and_deduplicated_by_financial_transaction_id()
    {
        await using var factory = CreateSePayFactory();
        using var client = CreateClient(factory);
        var providerId = $"93{Random.Shared.Next(100_000, 999_999)}";

        using var firstRequest = SePayRequest(providerId, 100_000);
        using var replayRequest = SePayRequest(providerId, 100_000);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(firstRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(replayRequest)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transaction = await context.ProviderTransactions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProviderTransactionId == providerId);
        Assert.Equal(
            2,
            await context.WebhookDeliveries.CountAsync(candidate =>
                candidate.ProviderTransactionId == transaction.Id));
    }

    [Fact]
    public async Task Sepay_same_identity_with_changed_payload_is_held_for_reconciliation()
    {
        await using var factory = CreateSePayFactory();
        using var client = CreateClient(factory);
        var providerId = $"94{Random.Shared.Next(100_000, 999_999)}";

        using var firstRequest = SePayRequest(providerId, 100_000);
        using var conflictRequest = SePayRequest(providerId, 101_000);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(firstRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(conflictRequest)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transaction = await context.ProviderTransactions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProviderTransactionId == providerId);
        Assert.Equal(
            ProviderTransactionProcessingStatuses.ReconciliationRequired,
            transaction.ProcessingStatus);
        Assert.Equal(
            SePayReconciliationReasons.ProviderTransactionConflict,
            transaction.ReconciliationReason);
    }

    [Fact]
    public async Task Sepay_invalid_hmac_and_stale_timestamp_are_rejected_without_persistence()
    {
        await using var factory = CreateSePayFactory();
        using var client = CreateClient(factory);
        var invalidId = $"95{Random.Shared.Next(100_000, 999_999)}";
        var staleId = $"96{Random.Shared.Next(100_000, 999_999)}";

        using var invalidRequest = SePayRequest(invalidId, 100_000);
        invalidRequest.Headers.Remove(SePayWebhookHeaders.Signature);
        invalidRequest.Headers.TryAddWithoutValidation(
            SePayWebhookHeaders.Signature,
            "sha256=" + new string('0', 64));
        using var staleRequest = SePayRequest(
            staleId,
            100_000,
            DateTimeOffset.UtcNow.AddMinutes(-6));

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.SendAsync(invalidRequest)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.SendAsync(staleRequest)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await context.ProviderTransactions.AnyAsync(candidate =>
            candidate.ProviderTransactionId == invalidId ||
            candidate.ProviderTransactionId == staleId));
    }

    [Fact]
    public async Task Sepay_webhook_rate_limit_returns_standardized_429()
    {
        await using var factory = CreateSePayFactory(sePayPermitLimitPerMinute: 1);
        using var client = CreateClient(factory);

        using var firstRequest = SePayRequest(
            $"97{Random.Shared.Next(100_000, 999_999)}",
            100_000);
        using var rejectedRequest = SePayRequest(
            $"98{Random.Shared.Next(100_000, 999_999)}",
            100_000);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(firstRequest)).StatusCode);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(rejectedRequest),
            HttpStatusCode.TooManyRequests,
            "RATE_LIMITED");
    }

    [Fact]
    public async Task Wallet_endpoints_require_authentication_and_return_only_owner_ledger()
    {
        using var anonymousClient = CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymousClient.GetAsync("/api/v1/wallet")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymousClient.GetAsync("/api/v1/wallet/entries")).StatusCode);

        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/wallet"));
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var userId = me.GetProperty("id").GetGuid();

        var balanceResponse = await client.GetAsync("/api/v1/wallet");
        Assert.Equal(HttpStatusCode.OK, balanceResponse.StatusCode);
        Assert.Equal("no-store", balanceResponse.Headers.CacheControl!.ToString());
        var balance = await balanceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, balance.GetProperty("paidAvailable").GetInt64());
        Assert.Equal(0, balance.GetProperty("paidReserved").GetInt64());
        Assert.Equal(0, balance.GetProperty("bonusAvailable").GetInt64());
        Assert.Equal(0, balance.GetProperty("totalAvailable").GetInt64());
        Assert.Equal(1, balance.GetProperty("version").GetInt64());

        var invalidResponse = await client.GetAsync("/api/v1/wallet/entries?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        var invalidProblem = await invalidResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", invalidProblem.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(invalidProblem.GetProperty("traceId").GetString()));

        await SeedWalletEntriesAsync(userId, 31);

        var firstResponse = await client.GetAsync("/api/v1/wallet/entries");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal("no-store", firstResponse.Headers.CacheControl!.ToString());
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(30, firstPage.GetProperty("items").GetArrayLength());
        var nextCursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.Equal(WalletQueryHandler.CursorLength, nextCursor!.Length);
        Assert.All(
            firstPage.GetProperty("items").EnumerateArray(),
            item => Assert.StartsWith(
                "OwnerCredit",
                item.GetProperty("operationType").GetString(),
                StringComparison.Ordinal));
        var firstPageIds = firstPage.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToHashSet();

        var secondPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/wallet/entries?cursor={Uri.EscapeDataString(nextCursor)}");
        Assert.Single(secondPage.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(
            secondPage.GetProperty("items")[0].GetProperty("id").GetGuid(),
            firstPageIds);
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Public_catalog_is_anonymous_hides_private_content_and_paginates_thirty_chapters()
    {
        var comicId = Guid.NewGuid();
        var draftComicId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var comic = Comic.Create(
                comicId,
                $"c04-api-{comicId:N}",
                "C04 API Catalog",
                "Public catalog detail",
                ComicSeriesStatuses.Ongoing,
                "covers/c04-api",
                25,
                true,
                now);
            for (var sortOrder = 1; sortOrder <= 31; sortOrder++)
            {
                var chapter = Chapter.Create(
                    Guid.NewGuid(),
                    comicId,
                    $"Chapter {sortOrder}",
                    sortOrder,
                    sortOrder == 1 ? 0 : null,
                    null,
                    now);
                var page = ChapterPage.CreatePending(
                    Guid.NewGuid(),
                    chapter.Id,
                    1,
                    $"c04-api/{comicId:N}/{sortOrder}",
                    $"sha256:{sortOrder:x64}",
                    now);
                page.MarkReady(1200, 1800);
                chapter.Publish(true, 1, now.AddSeconds(sortOrder));
                context.Chapters.Add(chapter);
                context.ChapterPages.Add(page);
            }

            comic.Publish(true, 1, now.AddMinutes(1));
            context.Comics.Add(comic);
            context.Comics.Add(Comic.Create(
                draftComicId,
                $"c04-draft-{draftComicId:N}",
                "C04 Draft",
                "Must remain private",
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                now));
            await context.SaveChangesAsync();
        }

        using var client = CreateClient();
        var list = await client.GetAsync("/api/v1/comics?search=C04%20API");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listDocument = await list.Content.ReadFromJsonAsync<JsonElement>();
        var listItem = Assert.Single(listDocument.GetProperty("items").EnumerateArray());
        Assert.Equal(comicId, listItem.GetProperty("id").GetGuid());

        var detail = await client.GetAsync($"/api/v1/comics/c04-api-{comicId:N}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailDocument = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Public catalog detail", detailDocument.GetProperty("description").GetString());

        var firstPage = await client.GetAsync($"/api/v1/comics/{comicId}/chapters");
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        var firstPageJson = await firstPage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(30, firstPageJson.GetProperty("items").GetArrayLength());
        Assert.Equal(1, firstPageJson.GetProperty("page").GetInt32());
        Assert.Equal(30, firstPageJson.GetProperty("pageSize").GetInt32());
        Assert.Equal(31, firstPageJson.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, firstPageJson.GetProperty("totalPages").GetInt32());
        var firstChapter = firstPageJson.GetProperty("items")[0];
        Assert.Equal(
            "Free",
            firstChapter.GetProperty("accessSummary").GetProperty("accessType").GetString());

        var secondPage = await client.GetAsync(
            $"/api/v1/comics/{comicId}/chapters?page=2");
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        var secondPageJson = await secondPage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, secondPageJson.GetProperty("items").GetArrayLength());
        Assert.Equal(2, secondPageJson.GetProperty("page").GetInt32());
        Assert.DoesNotContain("cloudinary", await firstPage.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliveryUrl", await firstPage.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        var invalidPage = await client.GetAsync(
            $"/api/v1/comics/{comicId}/chapters?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        var invalidPageDocument = await invalidPage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "VALIDATION_FAILED",
            invalidPageDocument.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            invalidPageDocument.GetProperty("message").GetString()));
        var hiddenDraft = await client.GetAsync(
            $"/api/v1/comics/c04-draft-{draftComicId:N}");
        Assert.Equal(HttpStatusCode.NotFound, hiddenDraft.StatusCode);
        var hiddenProblem = await hiddenDraft.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RESOURCE_NOT_FOUND", hiddenProblem.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(
            "Must remain private",
            await hiddenDraft.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_access_and_manifest_allow_free_and_deny_locked_or_hidden_content()
    {
        var comicId = Guid.NewGuid();
        var freeChapterId = Guid.NewGuid();
        var lockedChapterId = Guid.NewGuid();
        var hiddenChapterId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var comic = Comic.Create(
                comicId,
                $"r01-api-{comicId:N}",
                "R01 API",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                25,
                true,
                now);
            AddApiPublishedChapter(
                context,
                comicId,
                freeChapterId,
                1,
                0,
                now);
            AddApiPublishedChapter(
                context,
                comicId,
                lockedChapterId,
                2,
                null,
                now);
            var hidden = AddApiPublishedChapter(
                context,
                comicId,
                hiddenChapterId,
                3,
                40,
                now);
            hidden.Hide(canHide: true, expectedVersion: 2, now.AddMinutes(4));
            comic.Publish(true, 1, now.AddMinutes(5));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        using var anonymousClient = CreateClient();
        var freeAccess = await anonymousClient.GetAsync(
            $"/api/v1/chapters/{freeChapterId}/access");
        Assert.Equal(HttpStatusCode.OK, freeAccess.StatusCode);
        var freeAccessJson = await freeAccess.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Free", freeAccessJson.GetProperty("accessType").GetString());
        Assert.True(freeAccessJson.GetProperty("canRead").GetBoolean());
        Assert.False(freeAccessJson.GetProperty("needsLogin").GetBoolean());

        var freePages = await anonymousClient.GetAsync(
            $"/api/v1/chapters/{freeChapterId}/pages");
        Assert.Equal(HttpStatusCode.OK, freePages.StatusCode);
        Assert.Contains("no-store", freePages.Headers.CacheControl!.ToString());
        var freePagesJson = await freePages.Content.ReadFromJsonAsync<JsonElement>();
        var freePage = Assert.Single(freePagesJson.GetProperty("pages").EnumerateArray());
        Assert.Equal(1, freePage.GetProperty("order").GetInt32());
        Assert.Equal(1200, freePage.GetProperty("width").GetInt32());
        var deliveryUrl = freePage.GetProperty("deliveryUrl").GetString();
        Assert.Equal(
            $"/api/v1/chapters/{freeChapterId}/pages/1/content",
            deliveryUrl);
        Assert.False(freePagesJson.TryGetProperty("cloudinaryPublicId", out _));
        Assert.DoesNotContain(
            $"r01-api/{freeChapterId:N}/1",
            await freePages.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var freeContent = await anonymousClient.GetAsync(deliveryUrl);
        Assert.Equal(HttpStatusCode.OK, freeContent.StatusCode);
        Assert.Equal("image/webp", freeContent.Content.Headers.ContentType!.MediaType);
        Assert.Equal([1, 2, 3], await freeContent.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", freeContent.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(freeContent.Headers.GetValues("X-Content-Type-Options")));

        var lockedAccess = await anonymousClient.GetAsync(
            $"/api/v1/chapters/{lockedChapterId}/access");
        Assert.Equal(HttpStatusCode.OK, lockedAccess.StatusCode);
        var lockedAccessJson = await lockedAccess.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Locked", lockedAccessJson.GetProperty("accessType").GetString());
        Assert.True(lockedAccessJson.GetProperty("needsLogin").GetBoolean());

        var anonymousLockedPages = await anonymousClient.GetAsync(
            $"/api/v1/chapters/{lockedChapterId}/pages");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousLockedPages.StatusCode);
        var anonymousProblem =
            await anonymousLockedPages.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTH_REQUIRED", anonymousProblem.GetProperty("errorCode").GetString());
        var anonymousLockedContent = await anonymousClient.GetAsync(
            $"/api/v1/chapters/{lockedChapterId}/pages/1/content");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousLockedContent.StatusCode);

        _factory.TokenEndpoint.SetProfile(
            "google-subject-r01-reader",
            "R01 Reader",
            "r01-reader@example.com",
            null);
        using var authenticatedClient = CreateClient();
        await CompleteLoginAsync(
            authenticatedClient,
            await BeginLoginAsync(authenticatedClient, "/reader"));
        var authenticatedLockedPages = await authenticatedClient.GetAsync(
            $"/api/v1/chapters/{lockedChapterId}/pages");
        Assert.Equal(HttpStatusCode.Forbidden, authenticatedLockedPages.StatusCode);
        var forbiddenProblem =
            await authenticatedLockedPages.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FORBIDDEN", forbiddenProblem.GetProperty("errorCode").GetString());
        var authenticatedLockedContent = await authenticatedClient.GetAsync(
            $"/api/v1/chapters/{lockedChapterId}/pages/1/content");
        Assert.Equal(HttpStatusCode.Forbidden, authenticatedLockedContent.StatusCode);

        foreach (var path in new[]
                 {
                     $"/api/v1/chapters/{hiddenChapterId}/access",
                     $"/api/v1/chapters/{hiddenChapterId}/pages",
                     $"/api/v1/chapters/{hiddenChapterId}/pages/1/content",
                 })
        {
            var hiddenResponse = await anonymousClient.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, hiddenResponse.StatusCode);
            var hiddenProblem = await hiddenResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "CHAPTER_UNAVAILABLE",
                hiddenProblem.GetProperty("errorCode").GetString());
            Assert.DoesNotContain(
                "r01-api",
                await hiddenResponse.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Unlock_quote_requires_user_csrf_and_persists_server_pricing()
    {
        var comicId = Guid.NewGuid();
        var lockedChapterId = Guid.NewGuid();
        var freeChapterId = Guid.NewGuid();
        var vipEligibleChapterId = Guid.NewGuid();
        var hiddenChapterId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var comic = Comic.Create(
                comicId,
                $"u01-api-{comicId:N}",
                "U01 API",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                101,
                false,
                now);
            AddApiPublishedChapter(context, comicId, lockedChapterId, 1, null, now);
            AddApiPublishedChapter(context, comicId, freeChapterId, 2, 0, now);
            AddApiPublishedChapter(
                context,
                comicId,
                vipEligibleChapterId,
                3,
                null,
                now,
                vipOverride: true);
            var hidden = AddApiPublishedChapter(
                context,
                comicId,
                hiddenChapterId,
                4,
                null,
                now);
            hidden.Hide(canHide: true, expectedVersion: 2, now.AddMinutes(5));
            comic.Publish(true, 1, now.AddMinutes(3));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        using var anonymousClient = CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymousClient.PostAsync(
                $"/api/v1/chapters/{lockedChapterId}/unlock-quotes",
                content: null)).StatusCode);

        const string subject = "google-subject-u01-quote";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "U01 Quote User",
            "u01-quote@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/reader"));

        var missingCsrf = await client.PostAsync(
            $"/api/v1/chapters/{lockedChapterId}/unlock-quotes",
            content: null);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        var missingCsrfProblem = await missingCsrf.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "VALIDATION_FAILED",
            missingCsrfProblem.GetProperty("errorCode").GetString());

        var me = await client.GetAsync("/api/v1/me");
        var meJson = await me.Content.ReadFromJsonAsync<JsonElement>();
        var userId = meJson.GetProperty("id").GetGuid();
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        await using (var membershipScope = _factory.Services.CreateAsyncScope())
        {
            var context = membershipScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (subscription, _) = VipSubscription.Create(
                userId,
                DateTimeOffset.UtcNow,
                VipPlan.FixedDurationHours);
            context.VipSubscriptions.Add(subscription);
            await context.SaveChangesAsync();
        }

        async Task<HttpResponseMessage> CreateQuoteAsync(
            Guid chapterId,
            HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/chapters/{chapterId}/unlock-quotes")
            {
                Content = content,
            };
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        var clientOwnedExpiry = DateTimeOffset.UtcNow.AddYears(10);
        var injectedRevision = new string('a', 64);
        var response = await CreateQuoteAsync(
            lockedChapterId,
            JsonContent.Create(new
            {
                basePriceCoins = 1,
                vipDiscountApplied = false,
                finalPriceCoins = 1,
                expiresAt = clientOwnedExpiry,
                priceRevision = injectedRevision,
            }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(lockedChapterId, document.GetProperty("chapterId").GetGuid());
        Assert.Equal(101, document.GetProperty("basePriceCoins").GetInt64());
        Assert.True(document.GetProperty("vipDiscountApplied").GetBoolean());
        Assert.Equal(81, document.GetProperty("finalPriceCoins").GetInt64());
        Assert.Equal(0, document.GetProperty("bonusToSpend").GetInt64());
        Assert.Equal(0, document.GetProperty("paidToSpend").GetInt64());
        Assert.Matches(
            "^[0-9a-f]{64}$",
            document.GetProperty("priceRevision").GetString()!);
        Assert.NotEqual(
            injectedRevision,
            document.GetProperty("priceRevision").GetString());
        Assert.NotEqual(
            clientOwnedExpiry,
            document.GetProperty("expiresAt").GetDateTimeOffset());

        var activeVipAccess = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/chapters/{lockedChapterId}/access");
        Assert.Equal(81, activeVipAccess.GetProperty("priceCoins").GetInt64());

        await using (var verificationScope = _factory.Services.CreateAsyncScope())
        {
            var context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var quoteId = document.GetProperty("quoteId").GetGuid();
            var quote = await context.ChapterUnlockQuotes.AsNoTracking().SingleAsync(candidate =>
                candidate.Id == quoteId);
            Assert.Equal(TimeSpan.FromMinutes(5), quote.ExpiresAt - quote.CreatedAt);
            Assert.Equal(userId, quote.UserId);
        }

        var unnecessary = await CreateQuoteAsync(freeChapterId);
        Assert.Equal(HttpStatusCode.Conflict, unnecessary.StatusCode);
        var unnecessaryProblem = await unnecessary.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "ALREADY_UNLOCKED",
            unnecessaryProblem.GetProperty("errorCode").GetString());

        var vipIncluded = await CreateQuoteAsync(vipEligibleChapterId);
        Assert.Equal(HttpStatusCode.Conflict, vipIncluded.StatusCode);
        var vipIncludedProblem = await vipIncluded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "ALREADY_UNLOCKED",
            vipIncludedProblem.GetProperty("errorCode").GetString());

        var hiddenResponse = await CreateQuoteAsync(hiddenChapterId);
        await AssertStandardizedErrorAsync(
            hiddenResponse,
            HttpStatusCode.NotFound,
            "CHAPTER_UNAVAILABLE");

        var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using (var expireScope = _factory.Services.CreateAsyncScope())
        {
            var context = expireScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.VipSubscriptions
                .Where(subscription => subscription.UserId == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(subscription => subscription.ExpiresAt, expiredAt)
                    .SetProperty(
                        subscription => subscription.LastFulfilledAt,
                        expiredAt.AddHours(-VipPlan.FixedDurationHours)));
        }

        var expiredVipQuote = await CreateQuoteAsync(lockedChapterId);
        Assert.Equal(HttpStatusCode.Created, expiredVipQuote.StatusCode);
        var expiredVipDocument = await expiredVipQuote.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(expiredVipDocument.GetProperty("vipDiscountApplied").GetBoolean());
        Assert.Equal(101, expiredVipDocument.GetProperty("finalPriceCoins").GetInt64());
        var expiredVipAccess = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/chapters/{lockedChapterId}/access");
        Assert.Equal(101, expiredVipAccess.GetProperty("priceCoins").GetInt64());
    }

    [Fact]
    public async Task Unlock_quote_rate_limit_is_partitioned_by_authenticated_user()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var comic = Comic.Create(
                comicId,
                $"u01-rate-{comicId:N}",
                "U01 Rate Limit",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                100,
                false,
                now);
            AddApiPublishedChapter(context, comicId, chapterId, 1, null, now);
            comic.Publish(true, 1, now.AddMinutes(2));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        async Task<(HttpClient Client, string CsrfToken)> LoginAsync(string subject)
        {
            _factory.TokenEndpoint.SetProfile(subject, subject, $"{subject}@example.com", null);
            var loggedInClient = CreateClient();
            await CompleteLoginAsync(
                loggedInClient,
                await BeginLoginAsync(loggedInClient, "/reader"));
            var me = await loggedInClient.GetAsync("/api/v1/me");
            return (loggedInClient, Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN")));
        }

        static async Task<HttpResponseMessage> QuoteAsync(
            HttpClient client,
            string csrfToken,
            Guid candidateChapterId)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/chapters/{candidateChapterId}/unlock-quotes");
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        var firstUser = await LoginAsync("u01-rate-user-a");
        using (firstUser.Client)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var accepted = await QuoteAsync(
                    firstUser.Client,
                    firstUser.CsrfToken,
                    chapterId);
                Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            }

            using var rejected = await QuoteAsync(
                firstUser.Client,
                firstUser.CsrfToken,
                chapterId);
            await AssertStandardizedErrorAsync(
                rejected,
                HttpStatusCode.TooManyRequests,
                "RATE_LIMITED");
            Assert.True(rejected.Headers.RetryAfter is not null ||
                        rejected.Headers.TryGetValues("Retry-After", out _));
        }

        var secondUser = await LoginAsync("u01-rate-user-b");
        using (secondUser.Client)
        using (var accepted = await QuoteAsync(
                   secondUser.Client,
                   secondUser.CsrfToken,
                   chapterId))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
    }

    [Fact]
    public async Task Chapter_unlock_http_flow_enforces_auth_csrf_idempotency_and_server_pricing()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"u02-api-{comicId:N}",
                "U02 API",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                80,
                false,
                now);
            AddApiPublishedChapter(context, comicId, chapterId, 1, null, now);
            comic.Publish(true, 1, now.AddSeconds(2));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        using var anonymous = CreateClient();
        using var anonymousRequest = UnlockRequest(chapterId, Guid.NewGuid(), "anonymous");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.SendAsync(anonymousRequest)).StatusCode);

        const string subject = "google-subject-u02-unlock";
        _factory.TokenEndpoint.SetProfile(subject, "U02 User", "u02@example.com", null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/reader"));
        var me = await client.GetAsync("/api/v1/me");
        var csrf = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        var userId = (await me.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = ComicManagement.Wallet.Accounts.Wallet.Create(userId, now);
            var bonusOperation = WalletOperation.Create(
                Guid.NewGuid(), userId, "MissionReward", "RewardGrant",
                Guid.NewGuid(), $"u02-bonus-{Guid.NewGuid():N}", now);
            wallet.CreditBonus(30, now);
            var orderId = Guid.NewGuid();
            var order = PaymentOrder.CreateTopUp(
                orderId, userId, $"U02-{orderId:N}", 10_000, 1_000,
                $"u02-topup-{orderId:N}",
                new string('a', PaymentOrder.RequestFingerprintLength),
                now, now.AddMinutes(30));
            order.FulfillTopUp(now);
            var paidOperation = WalletOperation.Create(
                Guid.NewGuid(), userId, WalletOperationTypes.TopUpCredit,
                WalletOperationSourceTypes.PaymentOrder, orderId,
                $"u02-credit-{orderId:N}", now);
            wallet.CreditPaid(100, now);
            context.Wallets.Add(wallet);
            context.PaymentOrders.Add(order);
            context.PaidCoinLots.Add(PaidCoinLot.Create(
                Guid.NewGuid(), userId, orderId, 100, now));
            context.WalletOperations.AddRange(bonusOperation, paidOperation);
            context.WalletEntries.AddRange(
                WalletEntry.Create(
                    Guid.NewGuid(), bonusOperation, WalletCoinTypes.Bonus,
                    WalletBuckets.Available, 30, 1, now),
                WalletEntry.Create(
                    Guid.NewGuid(), paidOperation, WalletCoinTypes.Paid,
                    WalletBuckets.Available, 100, 1, now));
            await context.SaveChangesAsync();
        }

        async Task<Guid> QuoteAsync()
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/chapters/{chapterId}/unlock-quotes");
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("quoteId")
                .GetGuid();
        }

        var quoteId = await QuoteAsync();
        var otherQuoteId = await QuoteAsync();

        var expiredQuote = ChapterUnlockQuote.Create(
            Guid.NewGuid(),
            userId,
            chapterId,
            80,
            0,
            30,
            100,
            DateTimeOffset.UtcNow.AddMinutes(-10));
        await using (var expiredScope = _factory.Services.CreateAsyncScope())
        {
            var context = expiredScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.ChapterUnlockQuotes.Add(expiredQuote);
            await context.SaveChangesAsync();
        }

        using (var expiredRequest = UnlockRequest(
                   chapterId,
                   expiredQuote.Id,
                   "u02-expired",
                   csrf))
        {
            await AssertStandardizedErrorAsync(
                await client.SendAsync(expiredRequest),
                HttpStatusCode.Conflict,
                "QUOTE_EXPIRED");
        }

        var changedQuoteId = await QuoteAsync();
        await using (var changeScope = _factory.Services.CreateAsyncScope())
        {
            await changeScope.ServiceProvider.GetRequiredService<AppDbContext>().Chapters
                .Where(chapter => chapter.Id == chapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PriceOverrideCoins,
                    (long?)81));
        }

        using (var changedRequest = UnlockRequest(
                   chapterId,
                   changedQuoteId,
                   "u02-price-changed",
                   csrf))
        {
            await AssertStandardizedErrorAsync(
                await client.SendAsync(changedRequest),
                HttpStatusCode.Conflict,
                "PRICE_CHANGED");
        }

        await using (var restorePriceScope = _factory.Services.CreateAsyncScope())
        {
            await restorePriceScope.ServiceProvider.GetRequiredService<AppDbContext>().Chapters
                .Where(chapter => chapter.Id == chapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PriceOverrideCoins,
                    (long?)null));
        }

        var hiddenQuoteId = await QuoteAsync();
        await using (var hideScope = _factory.Services.CreateAsyncScope())
        {
            await hideScope.ServiceProvider.GetRequiredService<AppDbContext>().Chapters
                .Where(chapter => chapter.Id == chapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PublicationStatus,
                    ChapterPublicationStatuses.Hidden));
        }

        using (var hiddenRequest = UnlockRequest(
                   chapterId,
                   hiddenQuoteId,
                   "u02-hidden",
                   csrf))
        {
            await AssertStandardizedErrorAsync(
                await client.SendAsync(hiddenRequest),
                HttpStatusCode.NotFound,
                "CHAPTER_UNAVAILABLE");
        }

        await using (var publishScope = _factory.Services.CreateAsyncScope())
        {
            await publishScope.ServiceProvider.GetRequiredService<AppDbContext>().Chapters
                .Where(chapter => chapter.Id == chapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PublicationStatus,
                    ChapterPublicationStatuses.Published));
        }

        await using (var expensiveScope = _factory.Services.CreateAsyncScope())
        {
            await expensiveScope.ServiceProvider.GetRequiredService<AppDbContext>().Chapters
                .Where(chapter => chapter.Id == chapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PriceOverrideCoins,
                    (long?)200));
        }

        var expensiveQuoteId = await QuoteAsync();
        using (var insufficientRequest = UnlockRequest(
                   chapterId,
                   expensiveQuoteId,
                   "u02-insufficient",
                   csrf))
        {
            await AssertStandardizedErrorAsync(
                await client.SendAsync(insufficientRequest),
                HttpStatusCode.UnprocessableEntity,
                "INSUFFICIENT_COINS");
        }

        await using (var restoreScope = _factory.Services.CreateAsyncScope())
        {
            await restoreScope.ServiceProvider.GetRequiredService<AppDbContext>().Chapters
                .Where(chapter => chapter.Id == chapterId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    chapter => chapter.PriceOverrideCoins,
                    (long?)null));
        }

        using (var missingCsrf = UnlockRequest(chapterId, quoteId, "missing-csrf"))
        {
            await AssertStandardizedErrorAsync(
                await client.SendAsync(missingCsrf),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }

        using (var missingKey = UnlockRequest(chapterId, quoteId, null, csrf))
        {
            await AssertStandardizedErrorAsync(
                await client.SendAsync(missingKey),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }

        using (var emptyKey = UnlockRequest(chapterId, quoteId, string.Empty, csrf))
        {
            await AssertStandardizedErrorAsync(
                await client.SendAsync(emptyKey),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }

        using (var longKey = UnlockRequest(chapterId, quoteId, new string('k', 201), csrf))
        {
            await AssertStandardizedErrorAsync(
                await client.SendAsync(longKey),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }

        using (var malformed = UnlockRequest(chapterId, quoteId, "malformed", csrf))
        {
            malformed.Content = new StringContent(
                "{\"quoteId\":",
                Encoding.UTF8,
                "application/json");
            await AssertStandardizedErrorAsync(
                await client.SendAsync(malformed),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }

        using var request = UnlockRequest(chapterId, quoteId, "u02-http", csrf);
        request.Content = JsonContent.Create(new
        {
            quoteId,
            pricePaidCoins = 1,
            bonusSpentCoins = 999,
            paidSpentCoins = 999,
            accessType = "Free",
            unlockedAt = DateTimeOffset.UtcNow.AddYears(1),
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CoinUnlocked", body.GetProperty("accessType").GetString());
        Assert.Equal(30, body.GetProperty("spent").GetProperty("bonusCoins").GetInt64());
        Assert.Equal(50, body.GetProperty("spent").GetProperty("paidCoins").GetInt64());
        Assert.False(body.GetProperty("alreadyUnlocked").GetBoolean());

        using var replayRequest = UnlockRequest(chapterId, quoteId, "u02-http", csrf);
        var replay = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False((await replay.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("alreadyUnlocked")
            .GetBoolean());

        using var mismatchRequest = UnlockRequest(
            chapterId,
            otherQuoteId,
            "u02-http",
            csrf);
        await AssertStandardizedErrorAsync(
            await client.SendAsync(mismatchRequest),
            HttpStatusCode.Conflict,
            "IDEMPOTENCY_CONFLICT");

        var access = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/chapters/{chapterId}/access");
        Assert.Equal("CoinUnlocked", access.GetProperty("accessType").GetString());
    }

    [Fact]
    public async Task Chapter_unlock_returns_forbidden_without_user_role()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            useInsufficientRoleTestAuthentication: true);
        using var client = CreateClient(factory);
        using var request = UnlockRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "u02-insufficient-role");

        await AssertStandardizedErrorAsync(
            await client.SendAsync(request),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");
    }

    [Fact]
    public async Task Reading_session_requires_user_csrf_and_enforces_sequence_contract()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var comic = Comic.Create(
                comicId,
                $"r02-api-{comicId:N}",
                "R02 API",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                now);
            AddApiPublishedChapter(context, comicId, chapterId, 1, 0, now);
            comic.Publish(true, 1, now.AddMinutes(2));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        using var anonymousClient = CreateClient();
        var anonymous = await anonymousClient.PostAsJsonAsync(
            "/api/v1/reading/sessions",
            new { chapterId });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        _factory.TokenEndpoint.SetProfile(
            "google-subject-r02-reader",
            "R02 Reader",
            "r02-reader@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/reader"));

        var missingCsrf = await client.PostAsJsonAsync(
            "/api/v1/reading/sessions",
            new { chapterId });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/reading/sessions")
        {
            Content = JsonContent.Create(new { chapterId }),
        };
        createRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var created = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains("no-store", created.Headers.CacheControl!.ToString());
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = createdJson.GetProperty("sessionId").GetGuid();
        var startedAt = createdJson.GetProperty("startedAt").GetDateTimeOffset();
        var expiresAt = createdJson.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromMinutes(30), expiresAt - startedAt);
        Assert.Equal(
            15_000,
            createdJson.GetProperty("policy").GetProperty("minimumActiveDurationMs").GetInt64());
        Assert.Equal(1, createdJson.GetProperty("manifestPageCount").GetInt32());

        async Task<HttpResponseMessage> PatchAsync(decimal offset, long sequence)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/reading/sessions/{sessionId}")
            {
                Content = JsonContent.Create(new
                {
                    currentPageOrder = 1,
                    currentPageOffsetRatio = offset,
                    sequenceNumber = sequence,
                }),
            };
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        var first = await PatchAsync(0.95m, 1);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(9_500, firstJson.GetProperty("acceptedMaxProgressBasisPoints").GetInt32());
        Assert.Equal(0, firstJson.GetProperty("activeDurationMs").GetInt64());
        Assert.Equal(1, firstJson.GetProperty("lastAcceptedSequence").GetInt64());
        Assert.Equal(sessionId, firstJson.GetProperty("sessionId").GetGuid());
        Assert.Equal(1, firstJson.GetProperty("manifestPageCount").GetInt32());
        var persistedExpiresAt = firstJson.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(
            (persistedExpiresAt - expiresAt).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));

        var replay = await PatchAsync(0.95m, 1);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayJson = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, replayJson.GetProperty("activeDurationMs").GetInt64());

        var mismatch = await PatchAsync(0.9m, 1);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        var mismatchProblem = await mismatch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "READING_SEQUENCE_CONFLICT",
            mismatchProblem.GetProperty("errorCode").GetString());

        using var qualifyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/reading/sessions/{sessionId}/qualify");
        qualifyRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var qualification = await client.SendAsync(qualifyRequest);
        Assert.Equal(HttpStatusCode.OK, qualification.StatusCode);
        var qualificationJson = await qualification.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(qualificationJson.GetProperty("qualified").GetBoolean());
        Assert.Equal(
            "ACTIVE_DURATION_THRESHOLD_NOT_MET",
            qualificationJson.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Reading_progress_rate_limit_is_partitioned_after_authentication()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var comic = Comic.Create(
                comicId,
                $"r02-limit-{comicId:N}",
                "R02 Rate Limit",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                now);
            AddApiPublishedChapter(context, comicId, chapterId, 1, 0, now);
            comic.Publish(true, 1, now.AddMinutes(2));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            readingProgressPermitLimit: 2);
        factory.TokenEndpoint.SetProfile(
            "google-subject-r02-limit",
            "R02 Limit User",
            "r02-limit@example.com",
            null);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        await CompleteLoginAsync(
            client,
            await BeginLoginAsync(client, "/reader", factory.TokenEndpoint));
        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/reading/sessions")
        {
            Content = JsonContent.Create(new { chapterId }),
        };
        createRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var created = await client.SendAsync(createRequest);
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = createdJson.GetProperty("sessionId").GetGuid();

        async Task<HttpResponseMessage> PatchAsync(long sequence)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/reading/sessions/{sessionId}")
            {
                Content = JsonContent.Create(new
                {
                    currentPageOrder = 1,
                    currentPageOffsetRatio = 0.5m,
                    sequenceNumber = sequence,
                }),
            };
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(2)).StatusCode);
        var limited = await PatchAsync(3);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        var problem = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RATE_LIMITED", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Library_follow_requires_user_csrf_and_replays_idempotently()
    {
        var comicId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var comic = Comic.Create(
                comicId,
                $"r03-api-{comicId:N}",
                "R03 Library API",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                now);
            AddApiPublishedChapter(context, comicId, Guid.NewGuid(), 1, 0, now);
            comic.Publish(true, 1, now.AddMinutes(2));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        using var anonymousClient = CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymousClient.GetAsync("/api/v1/library/following")).StatusCode);

        _factory.TokenEndpoint.SetProfile(
            "google-subject-r03-library",
            "R03 Reader",
            "r03-reader@example.com",
            null);
        using var client = CreateClient();
        await CompleteLoginAsync(client, await BeginLoginAsync(client, "/library"));

        var missingCsrf = await client.PutAsync(
            $"/api/v1/comics/{comicId}/follow",
            content: null);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        var me = await client.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        async Task<HttpResponseMessage> FollowAsync()
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/comics/{comicId}/follow");
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        var created = await FollowAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(comicId, createdJson.GetProperty("comicId").GetGuid());

        var replay = await FollowAsync();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayJson = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            createdJson.GetProperty("followedAt").GetDateTimeOffset(),
            replayJson.GetProperty("followedAt").GetDateTimeOffset());

        var status = await client.GetAsync($"/api/v1/comics/{comicId}/follow-status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var statusJson = await status.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(statusJson.GetProperty("followed").GetBoolean());

        var followed = await client.GetAsync("/api/v1/library/following?limit=1");
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        var followedJson = await followed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(comicId, Assert.Single(
            followedJson.GetProperty("items").EnumerateArray()).GetProperty("comicId").GetGuid());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Delete,
                $"/api/v1/comics/{comicId}/follow");
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);
        }

        var unfollowedStatus = await client.GetAsync(
            $"/api/v1/comics/{comicId}/follow-status");
        var unfollowedStatusJson = await unfollowedStatus.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(unfollowedStatusJson.GetProperty("followed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unfollowedStatusJson.GetProperty("followedAt").ValueKind);
    }

    [Fact]
    public async Task Catalog_publication_requires_admin_csrf_and_returns_business_problem_details()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string subject = "google-subject-c03-admin";
        _factory.TokenEndpoint.SetProfile(
            subject,
            "Catalog Admin",
            "catalog-admin@example.com",
            null);

        using var anonymousClient = CreateClient();
        var anonymous = await anonymousClient.PostAsJsonAsync(
            $"/api/v1/admin/comics/{comicId}/publish",
            new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var userClient = CreateClient();
        await CompleteLoginAsync(userClient, await BeginLoginAsync(userClient, "/"));
        var forbidden = await userClient.PostAsJsonAsync(
            $"/api/v1/admin/comics/{comicId}/publish",
            new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

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

            var now = DateTimeOffset.UtcNow;
            context.Comics.Add(Comic.Create(
                comicId,
                $"c03-api-{comicId:N}",
                "C03 API",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                now));
            context.Chapters.Add(Chapter.Create(
                chapterId,
                comicId,
                "Chapter 1",
                1,
                0,
                false,
                now));
            var page = ChapterPage.CreatePending(
                Guid.NewGuid(),
                chapterId,
                1,
                $"c03-api/{chapterId:N}/1",
                $"sha256:{new string('a', 64)}",
                now);
            page.MarkReady(1200, 1800);
            context.ChapterPages.Add(page);
            await context.SaveChangesAsync();
        }

        using var adminClient = CreateClient();
        await CompleteLoginAsync(adminClient, await BeginLoginAsync(adminClient, "/"));

        var missingCsrf = await adminClient.PostAsJsonAsync(
            $"/api/v1/admin/comics/{comicId}/publish",
            new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        var missingCsrfProblem = await missingCsrf.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "VALIDATION_FAILED",
            missingCsrfProblem.GetProperty("errorCode").GetString());

        var me = await adminClient.GetAsync("/api/v1/me");
        var csrfToken = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        async Task<HttpResponseMessage> PostCommandAsync(string route, long expectedVersion)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(new { expectedVersion }),
            };
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            return await adminClient.SendAsync(request);
        }

        async Task<HttpResponseMessage> PatchSlugAsync(string slug, long expectedVersion)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/admin/comics/{comicId}/slug")
            {
                Content = JsonContent.Create(new { slug, expectedVersion }),
            };
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            return await adminClient.SendAsync(request);
        }

        var slugChange = await PatchSlugAsync("c03-api-renamed", 1);
        Assert.Equal(HttpStatusCode.OK, slugChange.StatusCode);
        var slugResponse = await slugChange.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("c03-api-renamed", slugResponse.GetProperty("slug").GetString());
        Assert.Equal(2, slugResponse.GetProperty("version").GetInt64());

        Assert.Equal(
            HttpStatusCode.OK,
            (await PostCommandAsync(
                $"/api/v1/admin/chapters/{chapterId}/publish",
                1)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostCommandAsync(
                $"/api/v1/admin/comics/{comicId}/publish",
                2)).StatusCode);

        var lastChapter = await PostCommandAsync(
            $"/api/v1/admin/chapters/{chapterId}/hide",
            2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lastChapter.StatusCode);
        var problem = await lastChapter.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "PUBLICATION_PRECONDITION_FAILED",
            problem.GetProperty("errorCode").GetString());
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
    public async Task OpenApi_document_contains_wallet_contracts_and_query_constraints()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        var balance = paths.GetProperty("/api/v1/wallet").GetProperty("get");
        foreach (var status in new[] { "200", "401", "403" })
        {
            Assert.True(balance.GetProperty("responses").TryGetProperty(status, out _));
        }

        var balanceSchema = ResolveResponseSchema(document, balance, "200");
        foreach (var property in new[]
                 {
                     "paidAvailable", "paidReserved", "bonusAvailable", "totalAvailable", "version",
                 })
        {
            Assert.True(balanceSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var entries = paths.GetProperty("/api/v1/wallet/entries").GetProperty("get");
        foreach (var status in new[] { "200", "400", "401", "403" })
        {
            Assert.True(entries.GetProperty("responses").TryGetProperty(status, out _));
        }

        var cursor = FindOpenApiParameter(entries, "cursor").GetProperty("schema");
        Assert.Equal(32, cursor.GetProperty("minLength").GetInt32());
        Assert.Equal(32, cursor.GetProperty("maxLength").GetInt32());
        var limit = FindOpenApiParameter(entries, "limit").GetProperty("schema");
        Assert.Equal(1, limit.GetProperty("minimum").GetInt32());
        Assert.Equal(100, limit.GetProperty("maximum").GetInt32());
        Assert.Equal(30, limit.GetProperty("default").GetInt32());

        var pageSchema = ResolveResponseSchema(document, entries, "200");
        Assert.True(pageSchema.GetProperty("properties").TryGetProperty("items", out var items));
        Assert.True(pageSchema.GetProperty("properties").TryGetProperty("nextCursor", out _));
        var itemSchema = ResolveOpenApiSchema(document, items.GetProperty("items"));
        foreach (var property in new[]
                 {
                     "id", "operationId", "operationType", "sourceType", "sourceId",
                     "coinType", "bucket", "deltaCoins", "occurredAt",
                 })
        {
            Assert.True(itemSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var errorSchema = ResolveResponseSchema(document, entries, "400");
        foreach (var property in new[] { "errorCode", "message", "traceId" })
        {
            Assert.True(errorSchema.GetProperty("properties").TryGetProperty(property, out _));
        }
    }

    [Fact]
    public async Task OpenApi_document_contains_topup_contract_and_idempotency_header()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var operation = document.GetProperty("paths")
            .GetProperty("/api/v1/billing/topups")
            .GetProperty("post");
        foreach (var status in new[] { "201", "400", "401", "403", "409", "429" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }

        var idempotency = FindOpenApiParameter(operation, "Idempotency-Key");
        Assert.Equal("header", idempotency.GetProperty("in").GetString());
        Assert.True(idempotency.GetProperty("required").GetBoolean());
        Assert.Equal(
            PaymentOrder.MaximumIdempotencyKeyLength,
            idempotency.GetProperty("schema").GetProperty("maxLength").GetInt32());

        var requestSchema = ResolveOpenApiSchema(
            document,
            operation.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        var amount = requestSchema.GetProperty("properties").GetProperty("amountVnd");
        Assert.Equal(TopUpAmountPolicy.MinimumAmountVnd, amount.GetProperty("minimum").GetInt64());
        Assert.Equal(TopUpAmountPolicy.MaximumAmountVnd, amount.GetProperty("maximum").GetInt64());
        Assert.Equal(TopUpAmountPolicy.AmountStepVnd, amount.GetProperty("multipleOf").GetInt64());

        var responseSchema = ResolveResponseSchema(document, operation, "201");
        foreach (var property in new[]
                 {
                     "orderId", "orderCode", "amountVnd", "paidCoinsExpected", "status",
                     "expiresAt", "paymentInstructions",
                 })
        {
            Assert.True(responseSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var statusSchema = ResolveOpenApiSchema(
            document,
            responseSchema.GetProperty("properties").GetProperty("status"));
        Assert.Equal(
            ["Pending", "Expired", "Succeeded"],
            statusSchema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        var instructionsSchema = ResolveOpenApiSchema(
            document,
            responseSchema.GetProperty("properties").GetProperty("paymentInstructions"));
        var instructionTypeSchema = ResolveOpenApiSchema(
            document,
            instructionsSchema.GetProperty("properties").GetProperty("type"));
        Assert.Equal(
            ["ProviderSpecific"],
            instructionTypeSchema.GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()));
        var errorSchema = ResolveResponseSchema(document, operation, "409");
        foreach (var property in new[] { "errorCode", "message", "traceId" })
        {
            Assert.True(errorSchema.GetProperty("properties").TryGetProperty(property, out _));
        }
    }

    [Fact]
    public async Task OpenApi_document_contains_vip_user_and_admin_contracts()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");

        var plan = paths.GetProperty("/api/v1/membership/plan").GetProperty("get");
        var planSchema = ResolveResponseSchema(document, plan, "200");
        Assert.Equal(
            ["discountPercent", "durationHours", "planId", "priceVnd", "version"],
            planSchema.GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()!).Order().ToArray());

        var membership = paths.GetProperty("/api/v1/membership/me").GetProperty("get");
        var membershipSchema = ResolveResponseSchema(document, membership, "200");
        AssertOpenApiSchemaIsNullable(ResolveOpenApiSchema(
            document,
            membershipSchema.GetProperty("properties").GetProperty("expiresAt")));

        var checkout = paths.GetProperty("/api/v1/membership/checkout")
            .GetProperty("post");
        var idempotency = FindOpenApiParameter(checkout, "Idempotency-Key");
        Assert.True(idempotency.GetProperty("required").GetBoolean());
        Assert.Equal(1, idempotency.GetProperty("schema").GetProperty("minLength").GetInt32());
        Assert.Equal(
            PaymentOrder.MaximumIdempotencyKeyLength,
            idempotency.GetProperty("schema").GetProperty("maxLength").GetInt32());
        var checkoutRequest = ResolveOpenApiSchema(
            document,
            checkout.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        Assert.Contains(
            "planId",
            checkoutRequest.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(
            "uuid",
            checkoutRequest.GetProperty("properties")
                .GetProperty("planId")
                .GetProperty("format")
                .GetString());
        var checkoutResponse = ResolveResponseSchema(document, checkout, "201");
        var checkoutProperties = checkoutResponse.GetProperty("properties");
        Assert.Equal(
            ["amountVnd", "durationHours", "expiresAt", "orderId", "paymentInstructions", "status"],
            checkoutResponse.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .Order()
                .ToArray());
        Assert.True(checkoutProperties.TryGetProperty("expiresAt", out _));
        Assert.True(checkoutProperties.TryGetProperty("paymentInstructions", out _));
        var status = ResolveOpenApiSchema(document, checkoutProperties.GetProperty("status"));
        Assert.Equal(
            ["Expired", "Pending", "Succeeded"],
            status.GetProperty("enum").EnumerateArray()
                .Select(item => item.GetString()!).Order().ToArray());
        foreach (var statusCode in new[] { "400", "401", "403", "404", "409", "429" })
        {
            Assert.True(checkout.GetProperty("responses").TryGetProperty(statusCode, out _));
            var errorSchema = ResolveResponseSchema(document, checkout, statusCode);
            foreach (var property in new[] { "errorCode", "message", "traceId" })
            {
                Assert.True(errorSchema.GetProperty("properties").TryGetProperty(property, out _));
            }
        }

        var adminPath = paths.GetProperty("/api/v1/admin/vip-plan");
        Assert.True(adminPath.TryGetProperty("get", out _));
        var patch = adminPath.GetProperty("patch");
        var patchRequest = ResolveOpenApiSchema(
            document,
            patch.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        var patchProperties = patchRequest.GetProperty("properties");
        Assert.Equal(1, patchProperties.GetProperty("priceVnd").GetProperty("minimum").GetInt64());
        Assert.Equal(1, patchProperties.GetProperty("expectedVersion").GetProperty("minimum").GetInt64());
        var concurrencyError = ResolveResponseSchema(document, patch, "409");
        foreach (var property in new[] { "errorCode", "message", "traceId" })
        {
            Assert.True(concurrencyError.GetProperty("properties").TryGetProperty(property, out _));
        }
    }

    [Fact]
    public async Task OpenApi_document_contains_admin_mission_contracts()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        var collection = paths.GetProperty("/api/v1/admin/missions");
        var create = collection.GetProperty("post");
        foreach (var status in new[] { "201", "400", "401", "403", "409", "429" })
        {
            Assert.True(create.GetProperty("responses").TryGetProperty(status, out _));
        }

        var createSchema = ResolveOpenApiSchema(
            document,
            create.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        var required = createSchema.GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(required.IsSupersetOf(["code", "name", "actionType", "cycleType"]));
        Assert.Equal(
            ["DailyLogin", "ChapterReadQualified", "ComicFollowed", "ChapterUnlocked", "WalletTopUpCompleted"],
            GetOpenApiEnumValues(
                document,
                createSchema.GetProperty("properties").GetProperty("actionType")));

        var revision = paths
            .GetProperty("/api/v1/admin/missions/{missionId}/revisions")
            .GetProperty("post");
        var revisionSchema = ResolveOpenApiSchema(
            document,
            revision.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        Assert.Equal(
            ["OncePerVietnamDate", "UniqueSubjectPerPeriod"],
            GetOpenApiEnumValues(
                document,
                revisionSchema.GetProperty("properties").GetProperty("repeatPolicy")));

        foreach (var path in new[]
                 {
                     "/api/v1/admin/missions/{missionId}",
                     "/api/v1/admin/missions/{missionId}/activate",
                     "/api/v1/admin/missions/{missionId}/deactivate",
                 })
        {
            Assert.True(paths.TryGetProperty(path, out _));
        }

        foreach (var operation in new[]
                 {
                     revision,
                     paths.GetProperty("/api/v1/admin/missions/{missionId}/activate")
                         .GetProperty("post"),
                     paths.GetProperty("/api/v1/admin/missions/{missionId}/deactivate")
                         .GetProperty("post"),
                 })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty("429", out _));
            var rateLimitError = ResolveResponseSchema(document, operation, "429");
            foreach (var property in new[] { "errorCode", "message", "traceId" })
            {
                Assert.True(rateLimitError.GetProperty("properties").TryGetProperty(
                    property,
                    out _));
            }
        }

        var errorSchema = ResolveResponseSchema(document, create, "400");
        foreach (var property in new[] { "errorCode", "message", "traceId" })
        {
            Assert.True(errorSchema.GetProperty("properties").TryGetProperty(property, out _));
        }
    }

    [Fact]
    public async Task OpenApi_document_contains_mission_progress_reward_and_pagination_contracts()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");

        var missions = paths.GetProperty("/api/v1/missions").GetProperty("get");
        var dailyLimit = paths.GetProperty("/api/v1/missions/daily-limit")
            .GetProperty("get");
        var rewards = paths.GetProperty("/api/v1/missions/rewards").GetProperty("get");
        foreach (var operation in new[] { missions, dailyLimit, rewards })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
            var unauthorized = ResolveResponseSchema(document, operation, "401");
            foreach (var property in new[] { "errorCode", "message", "traceId" })
            {
                Assert.True(unauthorized.GetProperty("properties")
                    .TryGetProperty(property, out _));
            }
        }

        var missionArray = ResolveResponseSchema(document, missions, "200");
        var missionItem = ResolveOpenApiSchema(document, missionArray.GetProperty("items"));
        foreach (var property in new[]
                 {
                     "missionId", "revisionId", "periodKey", "targetCount",
                     "progressCount", "rewardBonusCoins", "progressStatus", "rewardStatus",
                 })
        {
            Assert.True(missionItem.GetProperty("properties").TryGetProperty(property, out _));
        }
        Assert.Equal(
            ["InProgress", "Completed"],
            GetOpenApiEnumValues(
                document,
                missionItem.GetProperty("properties").GetProperty("progressStatus")));
        Assert.Equal(
            ["Pending", "Granted", "DeniedDailyCap"],
            GetOpenApiEnumValues(
                document,
                missionItem.GetProperty("properties").GetProperty("rewardStatus")));

        var limitSchema = ResolveResponseSchema(document, dailyLimit, "200");
        foreach (var property in new[]
                 {
                     "localDate", "limitCoins", "grantedCoins", "remainingCoins",
                 })
        {
            Assert.True(limitSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        Assert.True(rewards.GetProperty("responses").TryGetProperty("400", out _));
        var parameters = rewards.GetProperty("parameters").EnumerateArray().ToArray();
        var cursor = Assert.Single(parameters, item =>
            item.GetProperty("name").GetString() == "cursor").GetProperty("schema");
        Assert.Equal(32, cursor.GetProperty("minLength").GetInt32());
        Assert.Equal(32, cursor.GetProperty("maxLength").GetInt32());
        var limit = Assert.Single(parameters, item =>
            item.GetProperty("name").GetString() == "limit").GetProperty("schema");
        Assert.Equal(1, limit.GetProperty("minimum").GetInt32());
        Assert.Equal(100, limit.GetProperty("maximum").GetInt32());
        Assert.Equal(30, limit.GetProperty("default").GetInt32());

        var page = ResolveResponseSchema(document, rewards, "200");
        Assert.True(page.GetProperty("properties").TryGetProperty("items", out var items));
        Assert.True(page.GetProperty("properties").TryGetProperty("nextCursor", out _));
        var rewardItem = ResolveOpenApiSchema(
            document,
            items.GetProperty("items"));
        foreach (var property in new[]
                 {
                     "rewardGrantId", "missionId", "revisionId", "outcome",
                     "rewardBonusCoins", "grantedBonusCoins", "localDate", "decidedAt",
                 })
        {
            Assert.True(rewardItem.GetProperty("properties").TryGetProperty(property, out _));
        }
        Assert.Equal(
            ["Granted", "DeniedDailyCap"],
            GetOpenApiEnumValues(
                document,
                rewardItem.GetProperty("properties").GetProperty("outcome")));
    }

    [Fact]
    public async Task OpenApi_document_contains_sepay_raw_body_headers_and_ack_contract()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var operation = document.GetProperty("paths")
            .GetProperty("/api/v1/webhooks/sepay/ipn")
            .GetProperty("post");
        foreach (var status in new[] { "200", "400", "401", "413", "429", "503" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }

        foreach (var headerName in new[]
                 {
                     SePayWebhookHeaders.Signature,
                     SePayWebhookHeaders.Timestamp,
                 })
        {
            var header = FindOpenApiParameter(operation, headerName);
            Assert.Equal("header", header.GetProperty("in").GetString());
            Assert.True(header.GetProperty("required").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                header.GetProperty("schema").GetProperty("pattern").GetString()));
        }

        var requestSchema = ResolveOpenApiSchema(
            document,
            operation.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        foreach (var property in new[]
                 {
                     "id", "gateway", "transactionDate", "accountNumber", "subAccount",
                     "code", "content", "transferType", "description", "transferAmount",
                     "accumulated", "referenceCode",
                 })
        {
            Assert.True(requestSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var acknowledgement = ResolveResponseSchema(document, operation, "200");
        Assert.True(acknowledgement.GetProperty("properties").TryGetProperty("success", out _));
    }

    [Fact]
    public async Task OpenApi_document_contains_catalog_publication_contracts()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");

        foreach (var path in new[]
                 {
                     "/api/v1/admin/comics/{comicId}/publish",
                     "/api/v1/admin/comics/{comicId}/hide",
                     "/api/v1/admin/chapters/{chapterId}/publish",
                     "/api/v1/admin/chapters/{chapterId}/hide",
                 })
        {
            var post = paths.GetProperty(path).GetProperty("post");
            Assert.True(post.GetProperty("responses").TryGetProperty("200", out _));
            Assert.True(post.GetProperty("responses").TryGetProperty("400", out _));
            Assert.True(post.GetProperty("responses").TryGetProperty("401", out _));
            Assert.True(post.GetProperty("responses").TryGetProperty("403", out _));
            Assert.True(post.GetProperty("responses").TryGetProperty("404", out _));
            Assert.True(post.GetProperty("responses").TryGetProperty("409", out _));

            var requestSchema = ResolveOpenApiSchema(
                document,
                post.GetProperty("requestBody")
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema"));
            Assert.True(requestSchema.GetProperty("properties")
                .TryGetProperty("expectedVersion", out _));
            Assert.Contains(
                requestSchema.GetProperty("required").EnumerateArray(),
                property => property.GetString() == "expectedVersion");

            var responseSchema = ResolveOpenApiSchema(
                document,
                post.GetProperty("responses")
                    .GetProperty("200")
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema"));
            var responseProperties = responseSchema.GetProperty("properties");
            Assert.True(responseProperties.TryGetProperty("id", out _));
            Assert.True(responseProperties.TryGetProperty("publicationStatus", out _));
            Assert.True(responseProperties.TryGetProperty("version", out _));
            Assert.True(responseProperties.TryGetProperty("firstPublishedAt", out _));
        }

        Assert.True(paths
            .GetProperty("/api/v1/admin/comics/{comicId}/publish")
            .GetProperty("post")
            .GetProperty("responses")
            .TryGetProperty("422", out _));
        Assert.True(paths
            .GetProperty("/api/v1/admin/chapters/{chapterId}/publish")
            .GetProperty("post")
            .GetProperty("responses")
            .TryGetProperty("422", out _));
        Assert.True(paths
            .GetProperty("/api/v1/admin/chapters/{chapterId}/hide")
            .GetProperty("post")
            .GetProperty("responses")
            .TryGetProperty("422", out _));

        var patchSlug = paths
            .GetProperty("/api/v1/admin/comics/{comicId}/slug")
            .GetProperty("patch");
        foreach (var status in new[] { "200", "400", "401", "403", "404", "409", "422" })
        {
            Assert.True(patchSlug.GetProperty("responses").TryGetProperty(status, out _));
        }

        var slugRequestSchema = ResolveOpenApiSchema(
            document,
            patchSlug.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        Assert.True(slugRequestSchema.GetProperty("properties").TryGetProperty("slug", out _));
        Assert.True(slugRequestSchema.GetProperty("properties")
            .TryGetProperty("expectedVersion", out _));
        var slugResponseSchema = ResolveOpenApiSchema(
            document,
            patchSlug.GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        Assert.True(slugResponseSchema.GetProperty("properties").TryGetProperty("id", out _));
        Assert.True(slugResponseSchema.GetProperty("properties").TryGetProperty("slug", out _));
        Assert.True(slugResponseSchema.GetProperty("properties").TryGetProperty("version", out _));
    }

    [Fact]
    public async Task OpenApi_document_contains_public_catalog_contracts()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        var list = paths.GetProperty("/api/v1/comics").GetProperty("get");
        Assert.True(list.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(list.GetProperty("responses").TryGetProperty("400", out _));
        var listSchema = ResolveResponseSchema(document, list, "200");
        Assert.True(listSchema.GetProperty("properties").TryGetProperty("items", out _));
        Assert.True(listSchema.GetProperty("properties").TryGetProperty("nextCursor", out _));
        var listItemSchema = ResolveOpenApiSchema(
            document,
            listSchema.GetProperty("properties").GetProperty("items").GetProperty("items"));
        Assert.True(listItemSchema.GetProperty("properties")
            .TryGetProperty("updateSortAt", out _));
        var searchParameter = FindOpenApiParameter(list, "search");
        Assert.Equal(200, searchParameter.GetProperty("schema").GetProperty("maxLength").GetInt32());
        var genreParameter = FindOpenApiParameter(list, "genre");
        Assert.Equal(200, genreParameter.GetProperty("schema").GetProperty("maxLength").GetInt32());
        var seriesStatusParameter = FindOpenApiParameter(list, "seriesStatus");
        Assert.Equal(
            ["Ongoing", "Completed", "Hiatus"],
            seriesStatusParameter.GetProperty("schema").GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()));
        var cursorParameter = FindOpenApiParameter(list, "cursor");
        Assert.Equal(32, cursorParameter.GetProperty("schema").GetProperty("minLength").GetInt32());
        Assert.Equal(32, cursorParameter.GetProperty("schema").GetProperty("maxLength").GetInt32());
        var limitParameter = FindOpenApiParameter(list, "limit");
        Assert.Equal(1, limitParameter.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, limitParameter.GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.Equal(30, limitParameter.GetProperty("schema").GetProperty("default").GetInt32());
        var errorSchema = ResolveResponseSchema(document, list, "400");
        foreach (var property in new[] { "errorCode", "message", "traceId" })
        {
            Assert.True(errorSchema.GetProperty("properties").TryGetProperty(property, out _));
            Assert.Contains(
                property,
                errorSchema.GetProperty("required")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }

        var detail = paths.GetProperty("/api/v1/comics/{slug}").GetProperty("get");
        Assert.True(detail.GetProperty("responses").TryGetProperty("404", out _));
        var slugParameter = FindOpenApiParameter(detail, "slug");
        Assert.Equal(1, slugParameter.GetProperty("schema").GetProperty("minLength").GetInt32());
        Assert.Equal(200, slugParameter.GetProperty("schema").GetProperty("maxLength").GetInt32());
        var detailSchema = ResolveResponseSchema(document, detail, "200");
        foreach (var property in new[]
                 {
                     "id", "slug", "title", "description", "coverAssetId", "seriesStatus",
                     "firstPublishedAt", "updatedAt", "genres", "authors", "translationGroups",
                     "rating",
                 })
        {
            Assert.True(detailSchema.GetProperty("properties").TryGetProperty(property, out _));
        }
        var detailRating = ResolveOpenApiSchema(
            document,
            detailSchema.GetProperty("properties").GetProperty("rating"));
        foreach (var property in new[] { "averageStars", "ratingCount", "myRating" })
        {
            Assert.True(detailRating.GetProperty("properties").TryGetProperty(property, out _));
        }
        AssertOpenApiSchemaIsNullable(ResolveOpenApiSchema(
            document,
            detailRating.GetProperty("properties").GetProperty("myRating")));

        var chapters = paths
            .GetProperty("/api/v1/comics/{comicId}/chapters")
            .GetProperty("get");
        Assert.True(chapters.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(chapters.GetProperty("responses").TryGetProperty("404", out _));
        var pageParameter = FindOpenApiParameter(chapters, "page");
        Assert.Equal(1, pageParameter.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(
            PublicCatalogHandler.MaximumPublicChapterPage,
            pageParameter.GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.Equal(1, pageParameter.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.DoesNotContain(
            chapters.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() is "limit" or "cursor");
        var chapterPageSchema = ResolveResponseSchema(document, chapters, "200");
        foreach (var property in new[] { "page", "pageSize", "totalItems", "totalPages" })
        {
            Assert.True(chapterPageSchema.GetProperty("properties").TryGetProperty(property, out _));
        }
        var chapterItemsSchema = ResolveOpenApiSchema(
            document,
            chapterPageSchema.GetProperty("properties")
                .GetProperty("items")
                .GetProperty("items"));
        foreach (var property in new[]
                 {
                     "id", "chapterLabel", "sortOrder", "publicationStatus", "accessSummary",
                 })
        {
            Assert.True(chapterItemsSchema.GetProperty("properties")
                .TryGetProperty(property, out _));
        }

        var accessSummarySchema = ResolveOpenApiSchema(
            document,
            chapterItemsSchema.GetProperty("properties").GetProperty("accessSummary"));
        Assert.True(accessSummarySchema.GetProperty("properties")
            .TryGetProperty("accessType", out _));
        Assert.True(accessSummarySchema.GetProperty("properties")
            .TryGetProperty("canRead", out _));
        Assert.True(accessSummarySchema.GetProperty("properties")
            .TryGetProperty("finalPriceCoins", out _));
    }

    [Fact]
    public async Task OpenApi_document_contains_reader_access_and_manifest_contracts()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");

        var access = paths
            .GetProperty("/api/v1/chapters/{chapterId}/access")
            .GetProperty("get");
        Assert.True(access.GetProperty("responses").TryGetProperty("404", out _));
        var accessSchema = ResolveResponseSchema(document, access, "200");
        foreach (var property in new[]
                 {
                     "chapterId", "accessType", "canRead", "priceCoins", "needsLogin",
                 })
        {
            Assert.True(accessSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var accessTypeSchema = ResolveOpenApiSchema(
            document,
            accessSchema.GetProperty("properties").GetProperty("accessType"));
        Assert.Equal(
            ["Free", "VipIncluded", "CoinUnlocked", "Locked", "Unavailable"],
            accessTypeSchema.GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()));

        var quote = paths
            .GetProperty("/api/v1/chapters/{chapterId}/unlock-quotes")
            .GetProperty("post");
        foreach (var status in new[] { "201", "400", "401", "403", "404", "409", "429" })
        {
            Assert.True(quote.GetProperty("responses").TryGetProperty(status, out _));
        }

        var quoteSchema = ResolveResponseSchema(document, quote, "201");
        foreach (var property in new[]
                 {
                     "quoteId", "chapterId", "basePriceCoins", "vipDiscountApplied",
                     "finalPriceCoins", "bonusToSpend", "paidToSpend", "expiresAt",
                     "priceRevision",
                 })
        {
            Assert.True(quoteSchema.GetProperty("properties").TryGetProperty(property, out _));
            Assert.Contains(
                property,
                quoteSchema.GetProperty("required")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }

        var revisionSchema = quoteSchema.GetProperty("properties").GetProperty("priceRevision");
        Assert.Equal(64, revisionSchema.GetProperty("minLength").GetInt32());
        Assert.Equal(64, revisionSchema.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[0-9a-f]{64}$", revisionSchema.GetProperty("pattern").GetString());
        foreach (var status in new[] { "400", "401", "403", "404", "409", "429" })
        {
            var quoteErrorSchema = ResolveResponseSchema(document, quote, status);
            foreach (var property in new[] { "errorCode", "message", "traceId" })
            {
                Assert.True(
                    quoteErrorSchema.GetProperty("properties").TryGetProperty(property, out _));
            }
        }

        var unlock = paths
            .GetProperty("/api/v1/chapters/{chapterId}/unlock")
            .GetProperty("post");
        foreach (var status in new[] { "200", "400", "401", "403", "404", "409", "422" })
        {
            Assert.True(unlock.GetProperty("responses").TryGetProperty(status, out _));
        }

        var idempotencyHeader = unlock.GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(idempotencyHeader.GetProperty("required").GetBoolean());
        Assert.Equal(
            200,
            idempotencyHeader.GetProperty("schema").GetProperty("maxLength").GetInt32());
        var unlockRequestSchema = ResolveOpenApiSchema(
            document,
            unlock.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        Assert.Contains(
            "quoteId",
            unlockRequestSchema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(
            "uuid",
            unlockRequestSchema.GetProperty("properties")
                .GetProperty("quoteId")
                .GetProperty("format")
                .GetString());
        var unlockSchema = ResolveResponseSchema(document, unlock, "200");
        foreach (var property in new[]
                 {
                     "chapterId", "accessType", "spent", "walletAfter", "alreadyUnlocked",
                 })
        {
            Assert.True(unlockSchema.GetProperty("properties").TryGetProperty(property, out _));
            Assert.Contains(
                property,
                unlockSchema.GetProperty("required")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }

        var unlockAccessType = ResolveOpenApiSchema(
            document,
            unlockSchema.GetProperty("properties").GetProperty("accessType"));
        Assert.Equal(
            ["CoinUnlocked"],
            unlockAccessType.GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()));

        foreach (var status in new[] { "400", "401", "403", "404", "409", "422" })
        {
            var unlockError = ResolveResponseSchema(document, unlock, status);
            foreach (var property in new[] { "errorCode", "message", "traceId" })
            {
                Assert.True(unlockError.GetProperty("properties").TryGetProperty(property, out _));
            }
        }

        var pages = paths
            .GetProperty("/api/v1/chapters/{chapterId}/pages")
            .GetProperty("get");
        foreach (var status in new[] { "401", "403", "404", "503" })
        {
            Assert.True(pages.GetProperty("responses").TryGetProperty(status, out _));
        }

        var manifestSchema = ResolveResponseSchema(document, pages, "200");
        foreach (var property in new[]
                 {
                     "chapterId", "pages", "manifestIssuedAt", "refreshHint",
                 })
        {
            Assert.True(manifestSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var pageSchema = ResolveOpenApiSchema(
            document,
            manifestSchema.GetProperty("properties").GetProperty("pages").GetProperty("items"));
        foreach (var property in new[] { "order", "width", "height", "deliveryUrl" })
        {
            Assert.True(pageSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var content = paths
            .GetProperty("/api/v1/chapters/{chapterId}/pages/{pageOrder}/content")
            .GetProperty("get");
        foreach (var status in new[] { "200", "401", "403", "404", "503" })
        {
            Assert.True(content.GetProperty("responses").TryGetProperty(status, out _));
        }

        var errorSchema = ResolveResponseSchema(document, pages, "403");
        foreach (var property in new[] { "errorCode", "message", "traceId" })
        {
            Assert.True(errorSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var createSession = paths
            .GetProperty("/api/v1/reading/sessions")
            .GetProperty("post");
        foreach (var status in new[] { "201", "400", "401", "403", "404" })
        {
            Assert.True(createSession.GetProperty("responses").TryGetProperty(status, out _));
        }

        var createSessionSchema = ResolveResponseSchema(document, createSession, "201");
        foreach (var property in new[]
                 {
                     "sessionId", "startedAt", "expiresAt", "policy",
                     "manifestRevision", "manifestPageCount", "version",
                 })
        {
            Assert.True(createSessionSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var updateSession = paths
            .GetProperty("/api/v1/reading/sessions/{id}")
            .GetProperty("patch");
        foreach (var status in new[] { "200", "400", "401", "403", "404", "409", "429" })
        {
            Assert.True(updateSession.GetProperty("responses").TryGetProperty(status, out _));
        }

        var updateRequestSchema = ResolveOpenApiSchema(
            document,
            updateSession.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        var updateProperties = updateRequestSchema.GetProperty("properties");
        Assert.True(updateProperties.TryGetProperty("currentPageOrder", out var pageOrder));
        Assert.Equal(1, pageOrder.GetProperty("minimum").GetInt32());
        Assert.True(updateProperties.TryGetProperty(
            "currentPageOffsetRatio",
            out var pageOffset));
        Assert.Equal(0, pageOffset.GetProperty("minimum").GetDecimal());
        Assert.Equal(1, pageOffset.GetProperty("maximum").GetDecimal());
        Assert.True(updateProperties.TryGetProperty("sequenceNumber", out var sequence));
        Assert.Equal(1, sequence.GetProperty("minimum").GetInt64());
        Assert.False(updateProperties.TryGetProperty("progressBasisPoints", out _));
        Assert.False(updateProperties.TryGetProperty("maxObservedPageOrder", out _));

        var updateResponseSchema = ResolveResponseSchema(document, updateSession, "200");
        foreach (var property in new[]
                 {
                     "sessionId", "acceptedPosition", "acceptedMaxProgressBasisPoints",
                     "activeDurationMs", "manifestRevision", "manifestPageCount",
                     "lastAcceptedSequence", "expiresAt", "version",
                 })
        {
            Assert.True(updateResponseSchema.GetProperty("properties").TryGetProperty(
                property,
                out _));
        }

        var qualify = paths
            .GetProperty("/api/v1/reading/sessions/{id}/qualify")
            .GetProperty("post");
        Assert.True(qualify.GetProperty("responses").TryGetProperty("200", out _));
        var readingErrorSchema = ResolveResponseSchema(document, updateSession, "409");
        foreach (var property in new[] { "errorCode", "message", "traceId" })
        {
            Assert.True(readingErrorSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var follow = paths
            .GetProperty("/api/v1/comics/{comicId}/follow")
            .GetProperty("put");
        foreach (var status in new[] { "200", "400", "401", "403", "404" })
        {
            Assert.True(follow.GetProperty("responses").TryGetProperty(status, out _));
        }
        var followSchema = ResolveResponseSchema(document, follow, "200");
        foreach (var property in new[] { "comicId", "followed", "followedAt" })
        {
            Assert.True(followSchema.GetProperty("properties").TryGetProperty(property, out _));
        }
        Assert.False(followSchema.GetProperty("properties").TryGetProperty("created", out _));

        var unfollow = paths
            .GetProperty("/api/v1/comics/{comicId}/follow")
            .GetProperty("delete");
        foreach (var status in new[] { "204", "400", "401", "403" })
        {
            Assert.True(unfollow.GetProperty("responses").TryGetProperty(status, out _));
        }

        var followStatus = paths
            .GetProperty("/api/v1/comics/{comicId}/follow-status")
            .GetProperty("get");
        foreach (var status in new[] { "200", "400", "401", "403" })
        {
            Assert.True(followStatus.GetProperty("responses").TryGetProperty(status, out _));
        }
        var followStatusSchema = ResolveResponseSchema(document, followStatus, "200");
        foreach (var property in new[] { "comicId", "followed", "followedAt" })
        {
            Assert.True(followStatusSchema.GetProperty("properties").TryGetProperty(property, out _));
        }
        AssertOpenApiSchemaIsNullable(
            ResolveOpenApiSchema(
                document,
                followStatusSchema.GetProperty("properties").GetProperty("followedAt")));

        foreach (var operation in new[] { follow, unfollow, followStatus })
        {
            var followErrorSchema = ResolveResponseSchema(document, operation, "400");
            foreach (var property in new[] { "errorCode", "message", "traceId" })
            {
                Assert.True(followErrorSchema.GetProperty("properties").TryGetProperty(property, out _));
            }
        }

        foreach (var path in new[] { "/api/v1/library/continue", "/api/v1/library/following" })
        {
            var operation = paths.GetProperty(path).GetProperty("get");
            foreach (var status in new[] { "200", "400", "401", "403" })
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
            }
            var cursor = FindOpenApiParameter(operation, "cursor");
            Assert.Equal(
                LibraryHandler.CursorLength,
                cursor.GetProperty("schema").GetProperty("minLength").GetInt32());
            Assert.Equal(
                LibraryHandler.CursorLength,
                cursor.GetProperty("schema").GetProperty("maxLength").GetInt32());
            var limit = FindOpenApiParameter(operation, "limit");
            Assert.Equal(1, limit.GetProperty("schema").GetProperty("minimum").GetInt32());
            Assert.Equal(
                LibraryHandler.MaximumPageSize,
                limit.GetProperty("schema").GetProperty("maximum").GetInt32());
            Assert.Equal(
                LibraryHandler.DefaultPageSize,
                limit.GetProperty("schema").GetProperty("default").GetInt32());

            var libraryPageSchema = ResolveResponseSchema(document, operation, "200");
            foreach (var property in new[] { "items", "nextCursor" })
            {
                Assert.True(libraryPageSchema.GetProperty("properties").TryGetProperty(property, out _));
            }
            AssertOpenApiSchemaIsNullable(
                ResolveOpenApiSchema(
                    document,
                    libraryPageSchema.GetProperty("properties").GetProperty("nextCursor")));

            var itemSchema = ResolveOpenApiSchema(
                document,
                libraryPageSchema.GetProperty("properties").GetProperty("items").GetProperty("items"));
            foreach (var property in new[]
                     {
                         "comicId", "availability", "slug", "title", "coverAssetId", "seriesStatus",
                     })
            {
                Assert.True(itemSchema.GetProperty("properties").TryGetProperty(property, out _));
            }

            var availability = ResolveOpenApiSchema(
                document,
                itemSchema.GetProperty("properties").GetProperty("availability"));
            Assert.Equal(
                ["Available", "Unavailable"],
                availability.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

            foreach (var nullableProperty in new[] { "slug", "title", "coverAssetId", "seriesStatus" })
            {
                AssertOpenApiSchemaIsNullable(
                    ResolveOpenApiSchema(
                        document,
                        itemSchema.GetProperty("properties").GetProperty(nullableProperty)));
            }

            if (path.EndsWith("/continue", StringComparison.Ordinal))
            {
                foreach (var property in new[]
                         {
                             "chapterId", "chapterLabel", "pageOrder", "pageOffsetRatio",
                             "acceptedMaxProgressBasisPoints", "lastReadAt", "accessType", "canRead",
                         })
                {
                    Assert.True(itemSchema.GetProperty("properties").TryGetProperty(property, out _));
                }

                foreach (var nullableProperty in new[]
                         {
                             "chapterId", "chapterLabel", "pageOrder", "pageOffsetRatio",
                             "acceptedMaxProgressBasisPoints", "accessType",
                         })
                {
                    AssertOpenApiSchemaIsNullable(
                        ResolveOpenApiSchema(
                            document,
                            itemSchema.GetProperty("properties").GetProperty(nullableProperty)));
                }

                var acceptedMaximum = ResolveOpenApiSchema(
                    document,
                    itemSchema.GetProperty("properties")
                        .GetProperty("acceptedMaxProgressBasisPoints"));
                Assert.Equal(0, acceptedMaximum.GetProperty("minimum").GetInt32());
                Assert.Equal(10_000, acceptedMaximum.GetProperty("maximum").GetInt32());
            }
            else
            {
                Assert.True(itemSchema.GetProperty("properties").TryGetProperty("followedAt", out _));
            }

            var libraryErrorSchema = ResolveResponseSchema(document, operation, "400");
            foreach (var property in new[] { "errorCode", "message", "traceId" })
            {
                Assert.True(libraryErrorSchema.GetProperty("properties").TryGetProperty(property, out _));
            }
        }
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

    private static Chapter AddApiPublishedChapter(
        AppDbContext context,
        Guid comicId,
        Guid chapterId,
        int sortOrder,
        long? priceOverride,
        DateTimeOffset createdAt,
        bool? vipOverride = null)
    {
        var chapter = Chapter.Create(
            chapterId,
            comicId,
            $"Chapter {sortOrder}",
            sortOrder,
            priceOverride,
            vipOverride,
            createdAt);
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            1,
            $"r01-api/{chapterId:N}/1",
            $"sha256:{sortOrder:x64}",
            createdAt);
        page.MarkReady(1200, 1800);
        chapter.Publish(true, 1, createdAt.AddMinutes(sortOrder));
        context.Chapters.Add(chapter);
        context.ChapterPages.Add(page);
        return chapter;
    }

    private static JsonElement ResolveOpenApiSchema(
        JsonElement document,
        JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference))
        {
            return schema;
        }

        const string prefix = "#/components/schemas/";
        var value = reference.GetString()
            ?? throw new InvalidOperationException("OpenAPI schema reference is empty.");
        Assert.StartsWith(prefix, value, StringComparison.Ordinal);
        return document.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(value[prefix.Length..]);
    }

    private static void AssertStringLengthContract(
        JsonElement schema,
        int minimum,
        int maximum)
    {
        Assert.Equal(minimum, schema.GetProperty("minLength").GetInt32());
        Assert.Equal(maximum, schema.GetProperty("maxLength").GetInt32());
    }

    private static IReadOnlyList<string?> GetOpenApiEnumValues(
        JsonElement document,
        JsonElement schema)
    {
        var resolved = ResolveOpenApiSchema(document, schema);
        if (resolved.TryGetProperty("enum", out var values))
        {
            return values.EnumerateArray()
                .Where(value => value.ValueKind != JsonValueKind.Null)
                .Select(value => value.GetString())
                .ToArray();
        }

        foreach (var composition in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (!resolved.TryGetProperty(composition, out var options))
            {
                continue;
            }

            foreach (var option in options.EnumerateArray())
            {
                var candidate = ResolveOpenApiSchema(document, option);
                if (candidate.TryGetProperty("enum", out var candidateValues))
                {
                    return candidateValues.EnumerateArray()
                        .Where(value => value.ValueKind != JsonValueKind.Null)
                        .Select(value => value.GetString())
                        .ToArray();
                }
            }
        }

        throw new InvalidOperationException(
            $"OpenAPI schema does not declare enum values: {resolved.GetRawText()}");
    }

    private static JsonElement FindOpenApiParameter(JsonElement operation, string name) =>
        operation.GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == name);

    private static JsonElement ResolveResponseSchema(
        JsonElement document,
        JsonElement operation,
        string statusCode) => ResolveOpenApiSchema(
        document,
        operation.GetProperty("responses")
            .GetProperty(statusCode)
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema"));

    private static void AssertOpenApiSchemaIsNullable(JsonElement schema)
    {
        if (schema.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean())
        {
            return;
        }

        if (schema.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.Array &&
            type.EnumerateArray().Any(value => value.GetString() == "null"))
        {
            return;
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) &&
            anyOf.EnumerateArray().Any(option =>
                option.TryGetProperty("type", out var optionType) &&
                optionType.GetString() == "null"))
        {
            return;
        }

        Assert.Fail("Expected an OpenAPI nullable schema.");
    }

    private async Task<(Guid RefundId, long Version)> SeedRecoveryReservedRefundAsync(
        string suffix)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        var ownerId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var reserveOperationId = Guid.NewGuid();
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.AddRange(
            User.CreateFromGoogle(ownerId, $"RF03 Owner {suffix}", null, null, now.AddDays(-10)),
            User.CreateFromGoogle(reviewerId, $"RF03 Reviewer {suffix}", null, null,
                now.AddDays(-10)));
        var order = PaymentOrder.CreateTopUp(orderId, ownerId, $"ZENO-{orderId:N}",
            100_000, 10_000, $"rf03-http-order-{suffix}-{orderId:N}",
            new string('a', 64), now.AddMinutes(-10), now.AddMinutes(10));
        order.FulfillTopUp(now);
        var lot = PaidCoinLot.Create(lotId, ownerId, orderId, 10_000, now);
        lot.Reserve(10_000);
        var wallet = WalletAccount.Create(ownerId, now);
        wallet.CreditPaid(10_000, now);
        wallet.ReservePaid(10_000, now.AddSeconds(2));
        var credit = WalletOperation.Create(Guid.NewGuid(), ownerId,
            WalletOperationTypes.TopUpCredit, WalletOperationSourceTypes.PaymentOrder,
            orderId, null, now);
        var reserve = WalletOperation.Create(reserveOperationId, ownerId,
            WalletOperationTypes.RefundReserve, WalletOperationSourceTypes.RefundRequest,
            refundId, null, now.AddSeconds(2));
        var refund = RefundRequest.Create(refundId, orderId, ownerId, "Duplicate transfer",
            100_000, 10_000, $"rf03-http-refund-{suffix}", new string('b', 64), "v1", now);
        refund.BeginReview(reviewerId, now.AddSeconds(1));
        refund.ReserveRecovery(reviewerId, "Original lot verified", refund.Version,
            now.AddSeconds(2));
        var protectedRecipient = _factory.Services.GetRequiredService<IRefundRecipientProtector>()
            .Protect("0123456789", "NGUYEN VAN A");
        context.PaymentOrders.Add(order);
        context.PaidCoinLots.Add(lot);
        context.Wallets.Add(wallet);
        context.WalletOperations.AddRange(credit, reserve);
        context.WalletEntries.AddRange(
            WalletEntry.Create(Guid.NewGuid(), credit, WalletCoinTypes.Paid,
                WalletBuckets.Available, 10_000, 1, now),
            WalletEntry.Create(Guid.NewGuid(), reserve, WalletCoinTypes.Paid,
                WalletBuckets.Available, -10_000, 1, now.AddSeconds(2)),
            WalletEntry.Create(Guid.NewGuid(), reserve, WalletCoinTypes.Paid,
                WalletBuckets.Reserved, 10_000, 2, now.AddSeconds(2)));
        context.RefundRequests.Add(refund);
        context.RefundRecipients.Add(RefundRecipient.Create(Guid.NewGuid(), refundId, "VCB",
            protectedRecipient.AccountNumberCiphertext,
            protectedRecipient.AccountHolderCiphertext,
            "6789", "******6789", "N*** V*** A***",
            protectedRecipient.EncryptionKeyVersion, now));
        context.CoinReservations.Add(CoinReservation.Create(Guid.NewGuid(), refundId, ownerId,
            lotId, reserveOperationId, reviewerId, $"rf03-http-approval-{suffix}",
            new string('c', 64), 10_000, refund.Version, reviewerId,
            refund.ReviewedAt!.Value, "Original lot verified", now.AddSeconds(2)));
        await context.SaveChangesAsync();
        return (refundId, refund.Version);
    }

    private async Task SeedWalletEntriesAsync(Guid ownerUserId, int ownerEntryCount)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = new DateTimeOffset(2026, 10, 14, 11, 0, 0, TimeSpan.Zero);
        var otherUserId = Guid.NewGuid();
        context.Users.Add(User.CreateFromGoogle(
            otherUserId,
            "Other Wallet User",
            null,
            null,
            now));

        for (var index = 0; index < ownerEntryCount; index++)
        {
            var operation = WalletOperation.Create(
                Guid.NewGuid(),
                ownerUserId,
                $"OwnerCredit{index}",
                "ApiTest",
                Guid.NewGuid(),
                null,
                now);
            context.WalletOperations.Add(operation);
            context.WalletEntries.Add(WalletEntry.Create(
                Guid.NewGuid(),
                operation,
                WalletCoinTypes.Paid,
                WalletBuckets.Available,
                1,
                1,
                now));
        }

        var otherOperation = WalletOperation.Create(
            Guid.NewGuid(),
            otherUserId,
            "OtherCredit",
            "ApiTest",
            Guid.NewGuid(),
            null,
            now.AddMinutes(1));
        context.WalletOperations.Add(otherOperation);
        context.WalletEntries.Add(WalletEntry.Create(
            Guid.NewGuid(),
            otherOperation,
            WalletCoinTypes.Paid,
            WalletBuckets.Available,
            1,
            1,
            now.AddMinutes(1)));
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Comic_rating_http_flow_enforces_auth_csrf_validation_and_editable_uniqueness()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"in01-{comicId:N}",
                "IN01 Rating Comic",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                now);
            var chapter = Chapter.Create(
                chapterId,
                comicId,
                "Chapter 1",
                1,
                0,
                null,
                now);
            var page = ChapterPage.CreatePending(
                Guid.NewGuid(),
                chapterId,
                1,
                $"in01/{comicId:N}/1",
                $"sha256:{comicId:N}{comicId:N}",
                now);
            page.MarkReady(1200, 1800);
            chapter.Publish(true, 1, now.AddMinutes(1));
            comic.Publish(true, 1, now.AddMinutes(2));
            context.Comics.Add(comic);
            context.Chapters.Add(chapter);
            context.ChapterPages.Add(page);
            await context.SaveChangesAsync();
        }

        using var anonymous = CreateClient();
        var empty = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/v1/comics/{comicId}/rating");
        Assert.Equal(0, empty.GetProperty("ratingCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("myRating").ValueKind);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync(
                $"/api/v1/comics/{comicId}/rating",
                new { stars = 5 })).StatusCode);

        _factory.TokenEndpoint.SetProfile(
            $"in01-rating-{comicId:N}",
            "IN01 Rating User",
            $"in01-{comicId:N}@example.com",
            null);
        using var user = CreateClient();
        await CompleteLoginAsync(user, await BeginLoginAsync(user, $"/comics/in01-{comicId:N}"));
        var me = await user.GetAsync("/api/v1/me");
        var csrf = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));

        var missingCsrf = await user.PutAsJsonAsync(
            $"/api/v1/comics/{comicId}/rating",
            new { stars = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(
            "VALIDATION_FAILED",
            (await missingCsrf.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("errorCode").GetString());

        using (var invalid = new HttpRequestMessage(
                   HttpMethod.Put,
                   $"/api/v1/comics/{comicId}/rating")
               {
                   Content = JsonContent.Create(new { stars = 6 }),
               })
        {
            invalid.Headers.Add("X-CSRF-TOKEN", csrf);
            var response = await user.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "VALIDATION_FAILED",
                (await response.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("errorCode").GetString());
        }

        async Task<JsonElement> PutAsync(int stars)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/comics/{comicId}/rating")
            {
                Content = JsonContent.Create(new { stars }),
            };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            var response = await user.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        Assert.Equal(2, (await PutAsync(2)).GetProperty("myRating").GetInt32());
        var updated = await PutAsync(4);
        Assert.Equal(1, updated.GetProperty("ratingCount").GetInt32());
        Assert.Equal(4, updated.GetProperty("myRating").GetInt32());

        var detail = await user.GetFromJsonAsync<JsonElement>($"/api/v1/comics/in01-{comicId:N}");
        Assert.Equal(4, detail.GetProperty("rating").GetProperty("myRating").GetInt32());

        foreach (var invalidJson in new[]
                 {
                     "{}",
                     "{\"stars\":null}",
                     "{\"stars\":2.5}",
                     "{\"stars\":0}",
                     "{\"stars\":6}",
                     "{\"stars\":4,\"averageStars\":5}",
                     "{\"stars\":",
                 })
        {
            using var invalidRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/comics/{comicId}/rating")
            {
                Content = new StringContent(invalidJson, Encoding.UTF8, "application/json"),
            };
            invalidRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            await AssertStandardizedErrorAsync(
                await user.SendAsync(invalidRequest),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }

        using var delete = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/comics/{comicId}/rating");
        delete.Headers.Add("X-CSRF-TOKEN", csrf);
        var deleted = await user.SendAsync(delete);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var deletedBody = await deleted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, deletedBody.GetProperty("ratingCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, deletedBody.GetProperty("myRating").ValueKind);

        using var repeatedDelete = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/comics/{comicId}/rating");
        repeatedDelete.Headers.Add("X-CSRF-TOKEN", csrf);
        var repeatedDeleteResponse = await user.SendAsync(repeatedDelete);
        Assert.Equal(HttpStatusCode.OK, repeatedDeleteResponse.StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hidden = ComicPublicationStatuses.Hidden;
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE catalog.comics SET publication_status = {hidden} WHERE id = {comicId}");
        }

        await AssertStandardizedErrorAsync(
            await user.GetAsync($"/api/v1/comics/{comicId}/rating"),
            HttpStatusCode.NotFound,
            "RESOURCE_NOT_FOUND");
        await AssertStandardizedErrorAsync(
            await user.GetAsync($"/api/v1/comics/in01-{comicId:N}"),
            HttpStatusCode.NotFound,
            "RESOURCE_NOT_FOUND");

        var draftComicId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Comics.Add(Comic.Create(
                draftComicId,
                $"in01-draft-{draftComicId:N}",
                "IN01 Draft Comic",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                now));
            await context.SaveChangesAsync();
        }

        await AssertStandardizedErrorAsync(
            await user.GetAsync($"/api/v1/comics/{draftComicId}/rating"),
            HttpStatusCode.NotFound,
            "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Comic_rating_mutation_returns_forbidden_for_authenticated_principal_without_user_role()
    {
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            useInsufficientRoleTestAuthentication: true);
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/comics/{Guid.NewGuid()}/rating")
        {
            Content = JsonContent.Create(new { stars = 5 }),
        };

        await AssertStandardizedErrorAsync(
            await client.SendAsync(request),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");
    }

    [Fact]
    public async Task Comic_rating_get_and_detail_return_consistent_snapshot_during_concurrent_hide()
    {
        var ratingObserver = new BlockingRatingReadObserver();
        var detailObserver = new BlockingComicDetailReadObserver();
        await using var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(),
            _securityDirectory,
            comicRatingReadObserver: ratingObserver,
            comicDetailReadObserver: detailObserver);
        var (comicId, _) = await SeedPublicRatingComicAsync(factory.Services, "in01-race");
        using var client = CreateClient(factory);

        var ratingRead = client.GetAsync($"/api/v1/comics/{comicId}/rating");
        await ratingObserver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await HideComicAsync(factory.Services, comicId);
        ratingObserver.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await ratingRead).StatusCode);
        await AssertStandardizedErrorAsync(
            await client.GetAsync($"/api/v1/comics/{comicId}/rating"),
            HttpStatusCode.NotFound,
            "RESOURCE_NOT_FOUND");

        var (detailComicId, detailSlug) = await SeedPublicRatingComicAsync(
            factory.Services,
            "in01-detail-race");
        var detailRead = client.GetAsync($"/api/v1/comics/{detailSlug}");
        await detailObserver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await HideComicAsync(factory.Services, detailComicId);
        detailObserver.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await detailRead).StatusCode);
        await AssertStandardizedErrorAsync(
            await client.GetAsync($"/api/v1/comics/{detailSlug}"),
            HttpStatusCode.NotFound,
            "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Comic_rating_openapi_contract_declares_constraints_and_standard_errors()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/openapi/v1.json"));
        var path = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/comics/{comicId}/rating");
        var put = path.GetProperty("put");
        var requestSchema = ResolveOpenApiSchema(
            document.RootElement,
            put.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        var stars = ResolveOpenApiSchema(
            document.RootElement,
            requestSchema.GetProperty("properties").GetProperty("stars"));
        Assert.Equal(1, stars.GetProperty("minimum").GetInt32());
        Assert.Equal(5, stars.GetProperty("maximum").GetInt32());

        foreach (var method in new[] { "get", "put", "delete" })
        {
            var operation = path.GetProperty(method);
            var response = ResolveResponseSchema(document.RootElement, operation, "200");
            foreach (var property in new[] { "averageStars", "ratingCount", "myRating" })
            {
                Assert.True(response.GetProperty("properties").TryGetProperty(property, out _));
            }
        }

        var expectedResponses = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["get"] = ["200", "400", "404"],
            ["put"] = ["200", "400", "401", "403", "404"],
            ["delete"] = ["200", "400", "401", "403", "404"],
        };
        foreach (var (method, statuses) in expectedResponses)
        {
            var operation = path.GetProperty(method);
            foreach (var status in statuses)
            {
                Assert.True(
                    operation.GetProperty("responses").TryGetProperty(status, out _),
                    $"{method.ToUpperInvariant()} is missing {status}.");
                if (status == "200")
                {
                    continue;
                }

                var error = ResolveResponseSchema(document.RootElement, operation, status);
                foreach (var property in new[] { "errorCode", "message", "traceId" })
                {
                    Assert.True(error.GetProperty("properties").TryGetProperty(property, out _));
                }
            }
        }
    }

    [Fact]
    public async Task A01_admin_editor_requires_authorization_csrf_and_supports_create_read()
    {
        using var anonymous = CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/admin/comics")).StatusCode);

        var (admin, csrf) = await CreateAdminClientAsync($"a01-admin-{Guid.NewGuid():N}");
        using (admin)
        {
            var payload = new
            {
                slug = $"a01-{Guid.NewGuid():N}", title = "A01 Comic", description = "Editor",
                seriesStatus = "Ongoing", coverAssetId = (string?)null, defaultPriceCoins = 0,
                defaultVipEligible = false, genreIds = Array.Empty<Guid>(),
                authorIds = Array.Empty<Guid>(), translationGroupIds = Array.Empty<Guid>(),
            };
            Assert.Equal(HttpStatusCode.BadRequest,
                (await admin.PostAsJsonAsync("/api/v1/admin/comics", payload)).StatusCode);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/comics")
            { Content = JsonContent.Create(payload) };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            var created = await admin.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var item = await created.Content.ReadFromJsonAsync<AdminComicItem>();
            Assert.NotNull(item);
            Assert.Equal("A01 Comic", item.Title);

            using (var duplicateSlug = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/comics")
                   { Content = JsonContent.Create(payload) })
            {
                duplicateSlug.Headers.Add("X-CSRF-TOKEN", csrf);
                await AssertStandardizedErrorAsync(await admin.SendAsync(duplicateSlug),
                    HttpStatusCode.Conflict, "SLUG_CONFLICT");
            }
            var page = await admin.GetFromJsonAsync<AdminCatalogPage<AdminComicItem>>("/api/v1/admin/comics");
            Assert.Contains(page!.Items, comic => comic.Id == item.Id);
            var searched = await admin.GetFromJsonAsync<AdminCatalogPage<AdminComicItem>>(
                "/api/v1/admin/comics?search=A01&page=1&pageSize=30");
            Assert.Contains(searched!.Items, comic => comic.Id == item.Id);
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/comics?pageSize=101"),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/comics?publicationStatus=Unknown"),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");

            var invalidTaxonomyPayload = new
            {
                item.Slug, item.Title, item.Description, item.SeriesStatus, item.CoverAssetId,
                item.DefaultPriceCoins, item.DefaultVipEligible, expectedVersion = item.Version,
                genreIds = new[] { Guid.NewGuid() }, item.AuthorIds, item.TranslationGroupIds,
            };
            using var invalidTaxonomy = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/comics/{item.Id}")
            { Content = JsonContent.Create(invalidTaxonomyPayload) };
            invalidTaxonomy.Headers.Add("X-CSRF-TOKEN", csrf);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(invalidTaxonomy)).StatusCode);

            using (var blankCover = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/comics/{item.Id}")
                   { Content = JsonContent.Create(new { item.Slug, item.Title, item.Description, item.SeriesStatus,
                       coverAssetId = "   ", item.DefaultPriceCoins, item.DefaultVipEligible,
                       expectedVersion = item.Version, item.GenreIds, item.AuthorIds, item.TranslationGroupIds }) })
            {
                blankCover.Headers.Add("X-CSRF-TOKEN", csrf);
                await AssertStandardizedErrorAsync(await admin.SendAsync(blankCover),
                    HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            }

            using (var longTaxonomySlug = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/taxonomy/genres")
                   { Content = JsonContent.Create(new { name = "Too long", slug = new string('a', 201) }) })
            {
                longTaxonomySlug.Headers.Add("X-CSRF-TOKEN", csrf);
                await AssertStandardizedErrorAsync(await admin.SendAsync(longTaxonomySlug),
                    HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            }

            using var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/comics/{item.Id}")
            { Content = JsonContent.Create(new { item.Slug, item.Title, item.Description, item.SeriesStatus,
                item.CoverAssetId, item.DefaultPriceCoins, item.DefaultVipEligible, expectedVersion = 999L,
                item.GenreIds, item.AuthorIds, item.TranslationGroupIds }) };
            stale.Headers.Add("X-CSRF-TOKEN", csrf);
            await AssertStandardizedErrorAsync(await admin.SendAsync(stale),
                HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        }
    }

    [Fact]
    public async Task Comments_http_flow_enforces_reply_depth_ownership_moderation_and_audit()
    {
        var (comicId, _) = await SeedPublicRatingComicAsync(_factory.Services, "in02-comments");
        _factory.TokenEndpoint.SetProfile(
            $"in02-owner-{comicId:N}",
            "IN02 Owner",
            $"in02-owner-{comicId:N}@example.com",
            null);
        using var owner = CreateClient();
        await CompleteLoginAsync(owner, await BeginLoginAsync(owner, $"/comics/in02-comments-{comicId:N}"));
        var ownerProfile = await owner.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var ownerId = ownerProfile.GetProperty("id").GetGuid();
        var ownerCsrf = Assert.Single((await owner.GetAsync("/api/v1/me")).Headers.GetValues("X-CSRF-TOKEN"));

        async Task<HttpResponseMessage> OwnerMutationAsync(HttpMethod method, string path, object body)
        {
            var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", ownerCsrf);
            return await owner.SendAsync(request);
        }

        var createdResponse = await OwnerMutationAsync(
            HttpMethod.Post,
            "/api/v1/comments",
            new { comicId, body = "  Bình luận gốc  " });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var root = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rootId = root.GetProperty("id").GetGuid();
        Assert.Equal("Bình luận gốc", root.GetProperty("body").GetString());
        Assert.Equal(ownerId, root.GetProperty("userId").GetGuid());

        var replyResponse = await OwnerMutationAsync(
            HttpMethod.Post,
            "/api/v1/comments",
            new { comicId, parentId = rootId, body = "Phản hồi" });
        Assert.Equal(HttpStatusCode.Created, replyResponse.StatusCode);
        var reply = await replyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var replyId = reply.GetProperty("id").GetGuid();

        await AssertStandardizedErrorAsync(
            await OwnerMutationAsync(
                HttpMethod.Post,
                "/api/v1/comments",
                new { comicId, parentId = replyId, body = "Không cho reply cấp hai" }),
            HttpStatusCode.UnprocessableEntity,
            "COMMENT_PARENT_INVALID");

        await AssertStandardizedErrorAsync(
            await OwnerMutationAsync(
                HttpMethod.Post,
                "/api/v1/comments",
                new { comicId, body = "unexpected", price = 123 }),
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");

        var publicPage = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/comics/{comicId}/comments?page=1");
        Assert.Equal(20, publicPage.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, publicPage.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, publicPage.GetProperty("items")[0].GetProperty("replyCount").GetInt32());
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>($"/api/v1/comments/{rootId}/replies?page=1"))
            .GetProperty("items").EnumerateArray());

        _factory.TokenEndpoint.SetProfile(
            $"in02-other-{comicId:N}",
            "IN02 Other",
            $"in02-other-{comicId:N}@example.com",
            null);
        using var other = CreateClient();
        await CompleteLoginAsync(other, await BeginLoginAsync(other, "/"));
        var otherProfileResponse = await other.GetAsync("/api/v1/me");
        var otherCsrf = Assert.Single(otherProfileResponse.Headers.GetValues("X-CSRF-TOKEN"));
        using (var forbidden = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/comments/{rootId}")
               {
                   Content = JsonContent.Create(new { body = "chiếm quyền", expectedVersion = 1 }),
               })
        {
            forbidden.Headers.Add("X-CSRF-TOKEN", otherCsrf);
            await AssertStandardizedErrorAsync(
                await other.SendAsync(forbidden),
                HttpStatusCode.Forbidden,
                "FORBIDDEN");
        }

        var ownerEdit = await OwnerMutationAsync(
            HttpMethod.Patch,
            $"/api/v1/comments/{rootId}",
            new { body = "Bình luận đã sửa", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, ownerEdit.StatusCode);
        Assert.Equal(2, (await ownerEdit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
        await AssertStandardizedErrorAsync(
            await OwnerMutationAsync(
                HttpMethod.Patch,
                $"/api/v1/comments/{rootId}",
                new { body = "Stale", expectedVersion = 1 }),
            HttpStatusCode.Conflict,
            "CONCURRENCY_CONFLICT");

        var deleteCandidate = await OwnerMutationAsync(
            HttpMethod.Post,
            "/api/v1/comments",
            new { comicId, body = "Bình luận sẽ xóa" });
        var deleteCandidateId = (await deleteCandidate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var ownerDelete = await OwnerMutationAsync(
            HttpMethod.Delete,
            $"/api/v1/comments/{deleteCandidateId}",
            new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, ownerDelete.StatusCode);
        Assert.Equal("Deleted", (await ownerDelete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var (admin, adminCsrf) = await CreateAdminClientAsync($"in02-admin-{comicId:N}");
        using (admin)
        {
            var moderationPage = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/comments?page=1");
            var replyContext = moderationPage.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == replyId);
            Assert.Equal("IN01 Race Comic", replyContext.GetProperty("targetTitle").GetString());
            Assert.Equal("Bình luận đã sửa", replyContext.GetProperty("parentBody").GetString());
            Assert.Equal("IN02 Owner", replyContext.GetProperty("parentAuthorDisplayName").GetString());

            async Task<JsonElement> ModerateAsync(string action, long version)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/v1/admin/comments/{rootId}/{action}")
                {
                    Content = JsonContent.Create(new { expectedVersion = version, reason = $"IN02 {action}" }),
                };
                request.Headers.Add("X-CSRF-TOKEN", adminCsrf);
                var response = await admin.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            }

            var hidden = await ModerateAsync("hide", 2);
            Assert.Equal("Hidden", hidden.GetProperty("status").GetString());
            Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/v1/comics/{comicId}/comments"))
                .GetProperty("items").EnumerateArray());
            var restored = await ModerateAsync("restore", 3);
            Assert.Equal("Visible", restored.GetProperty("status").GetString());
            var deleted = await ModerateAsync("delete", 4);
            Assert.Equal("Deleted", deleted.GetProperty("status").GetString());

            using var restoreDeleted = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/admin/comments/{rootId}/restore")
            {
                Content = JsonContent.Create(new { expectedVersion = 5, reason = "Không được phục hồi deleted" }),
            };
            restoreDeleted.Headers.Add("X-CSRF-TOKEN", adminCsrf);
            await AssertStandardizedErrorAsync(
                await admin.SendAsync(restoreDeleted),
                HttpStatusCode.Conflict,
                "COMMENT_STATE_CONFLICT");
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actions = await context.AuditLogs
            .Where(entry => entry.TargetId == rootId)
            .Select(entry => entry.Action)
            .ToListAsync();
        Assert.Equal(
            ["Interaction.CommentHidden", "Interaction.CommentRestored", "Interaction.CommentDeleted"],
            actions);
    }

    [Fact]
    public async Task Comment_mutations_require_auth_csrf_and_are_rate_limited_per_principal()
    {
        using var anonymous = CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/v1/comments", new { comicId = Guid.NewGuid(), body = "x" })).StatusCode);
        await AssertStandardizedErrorAsync(
            await anonymous.GetAsync("/api/v1/admin/comments"),
            HttpStatusCode.Unauthorized,
            "AUTH_REQUIRED");

        _factory.TokenEndpoint.SetProfile(
            $"in02-rate-{Guid.NewGuid():N}",
            "IN02 Rate User",
            null,
            null);
        using var user = CreateClient();
        await CompleteLoginAsync(user, await BeginLoginAsync(user, "/"));
        var me = await user.GetAsync("/api/v1/me");
        var csrf = Assert.Single(me.Headers.GetValues("X-CSRF-TOKEN"));
        await AssertStandardizedErrorAsync(
            await user.GetAsync("/api/v1/admin/comments"),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");

        var missingCsrf = await user.PostAsJsonAsync(
            "/api/v1/comments",
            new { comicId = Guid.NewGuid(), body = "x" });
        await AssertStandardizedErrorAsync(missingCsrf, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        // The rejected CSRF request above already consumed one of this endpoint's
        // twenty middleware-level permits for the authenticated principal.
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/comments")
            {
                Content = JsonContent.Create(new { comicId = Guid.NewGuid(), body = "x" }),
            };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            var response = await user.SendAsync(request);
            if (attempt <= 19)
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            else
            {
                await AssertStandardizedErrorAsync(response, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
            }
        }
    }

    [Fact]
    public async Task A01_admin_catalog_returns_standardized_forbidden_and_rate_limited_errors()
    {
        await using (var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(), _securityDirectory,
            useInsufficientRoleTestAuthentication: true))
        using (var client = CreateClient(factory))
        {
            await AssertStandardizedErrorAsync(await client.GetAsync("/api/v1/admin/comics"),
                HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        var (admin, csrf) = await CreateAdminClientAsync($"a01-rate-{Guid.NewGuid():N}");
        using (admin)
        {
            HttpResponseMessage? response = null;
            for (var index = 0; index < 21; index++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/taxonomy/not-a-kind")
                { Content = JsonContent.Create(new { name = "Invalid", slug = "invalid" }) };
                request.Headers.Add("X-CSRF-TOKEN", csrf);
                response = await admin.SendAsync(request);
                if (index < 20) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            await AssertStandardizedErrorAsync(response!, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        }
    }

    [Fact]
    public async Task A01_chapter_page_http_workflow_appends_and_rejects_stale_version()
    {
        var (admin, csrf) = await CreateAdminClientAsync($"a01-pages-{Guid.NewGuid():N}");
        using (admin)
        {
            var comicPayload = new
            {
                slug = $"a01-pages-{Guid.NewGuid():N}", title = "Pages", description = "Page editor test",
                seriesStatus = "Ongoing", coverAssetId = (string?)null, defaultPriceCoins = 0,
                defaultVipEligible = false, genreIds = Array.Empty<Guid>(), authorIds = Array.Empty<Guid>(),
                translationGroupIds = Array.Empty<Guid>(),
            };
            using var comicRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/comics")
            { Content = JsonContent.Create(comicPayload) };
            comicRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            var comicResponse = await admin.SendAsync(comicRequest);
            Assert.True(comicResponse.StatusCode == HttpStatusCode.Created,
                $"Expected comic create, got {comicResponse.StatusCode}: {await comicResponse.Content.ReadAsStringAsync()}");
            var comic = await comicResponse.Content.ReadFromJsonAsync<AdminComicItem>();
            Assert.NotNull(comic);

            using var chapterRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/chapters")
            { Content = JsonContent.Create(new { comicId = comic.Id, chapterLabel = "1", sortOrder = 1,
                priceOverrideCoins = (long?)null, vipOverride = (bool?)null, expectedVersion = (long?)null }) };
            chapterRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            var chapterResponse = await admin.SendAsync(chapterRequest);
            Assert.True(chapterResponse.StatusCode == HttpStatusCode.Created,
                $"Expected chapter create, got {chapterResponse.StatusCode}: {await chapterResponse.Content.ReadAsStringAsync()}");
            var chapter = await chapterResponse.Content.ReadFromJsonAsync<AdminChapterItem>();
            Assert.NotNull(chapter);

            using var upload = PageUploadRequest(chapter.Id, chapter.Version, csrf, "first.png", [1, 2, 3]);
            var uploaded = await admin.SendAsync(upload);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            var manifest = await uploaded.Content.ReadFromJsonAsync<AdminPageManifest>();
            Assert.NotNull(manifest);
            Assert.Equal(1, Assert.Single(manifest.Pages).PageOrder);

            using var stale = PageUploadRequest(chapter.Id, chapter.Version, csrf, "stale.png", [4]);
            var conflict = await admin.SendAsync(stale);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("CONCURRENCY_CONFLICT",
                (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

            using var invalidType = PageUploadRequest(chapter.Id, manifest.Version, csrf, "not-image.txt", [5], "text/plain");
            await AssertStandardizedErrorAsync(await admin.SendAsync(invalidType),
                HttpStatusCode.BadRequest, "VALIDATION_FAILED");

            using var invalid = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/chapters/{chapter.Id}/pages")
            { Content = new MultipartFormDataContent { { new StringContent(manifest.Version.ToString()), "expectedVersion" } } };
            invalid.Headers.Add("X-CSRF-TOKEN", csrf);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(invalid)).StatusCode);
        }
    }

    [Fact]
    public async Task A01_invalid_taxonomy_kind_is_standardized_validation_error()
    {
        var (admin, _) = await CreateAdminClientAsync($"a01-taxonomy-{Guid.NewGuid():N}");
        using (admin)
        {
            var response = await admin.GetAsync("/api/v1/admin/taxonomy/not-a-kind");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());
        }
    }

    [Fact]
    public async Task A01_openapi_declares_multipart_versioning_and_standardized_errors()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/admin/chapters/{chapterId}/pages").GetProperty("post");
        var multipart = ResolveOpenApiSchema(document.RootElement,
            operation.GetProperty("requestBody").GetProperty("content")
                .GetProperty("multipart/form-data").GetProperty("schema"));
        Assert.True(multipart.GetProperty("properties").TryGetProperty("expectedVersion", out _));
        Assert.True(multipart.GetProperty("properties").TryGetProperty("files", out var files));
        Assert.Equal("array", files.GetProperty("type").GetString());

        foreach (var status in new[] { "400", "401", "403", "404", "409", "422", "429", "503" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), $"Missing {status}");
            var schema = ResolveResponseSchema(document.RootElement, operation, status);
            foreach (var property in new[] { "errorCode", "message", "traceId" })
                Assert.True(schema.GetProperty("properties").TryGetProperty(property, out _));
        }

        var comicList = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/admin/comics").GetProperty("get");
        var listParameters = comicList.GetProperty("parameters").EnumerateArray().ToArray();
        var pageSize = Assert.Single(listParameters, parameter =>
            parameter.GetProperty("name").GetString() == "pageSize").GetProperty("schema");
        Assert.Equal(1, pageSize.GetProperty("minimum").GetInt32());
        Assert.Equal(100, pageSize.GetProperty("maximum").GetInt32());
        Assert.True(comicList.GetProperty("responses").TryGetProperty("400", out _));

        foreach (var (path, method, statuses) in new[]
        {
            ("/api/v1/admin/chapters/{chapterId}/pages/order", "put", new[] { "400", "401", "403", "404", "409", "422", "429" }),
            ("/api/v1/admin/chapters/{chapterId}/pages/{pageId}/content", "put", new[] { "400", "401", "403", "404", "409", "429", "503" }),
            ("/api/v1/admin/chapters/{chapterId}/pages/{pageId}", "delete", new[] { "400", "401", "403", "404", "409", "422", "429" }),
        })
        {
            var pageOperation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);
            foreach (var status in statuses)
            {
                Assert.True(pageOperation.GetProperty("responses").TryGetProperty(status, out _),
                    $"Missing {method.ToUpperInvariant()} {path} response {status}");
                var schema = ResolveResponseSchema(document.RootElement, pageOperation, status);
                foreach (var property in new[] { "errorCode", "message", "traceId" })
                    Assert.True(schema.GetProperty("properties").TryGetProperty(property, out _));
            }
        }
    }

    [Fact]
    public async Task Comment_openapi_contract_declares_pagination_body_constraints_and_standard_errors()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        foreach (var path in new[]
                 {
                     "/api/v1/comics/{comicId}/comments",
                     "/api/v1/chapters/{chapterId}/comments",
                     "/api/v1/comments/{commentId}/replies",
                 })
        {
            var get = paths.GetProperty(path).GetProperty("get");
            var page = FindOpenApiParameter(get, "page").GetProperty("schema");
            Assert.Equal(1, page.GetProperty("minimum").GetInt32());
            Assert.Equal(100000, page.GetProperty("maximum").GetInt32());
            Assert.Equal(1, page.GetProperty("default").GetInt32());
            AssertCommentResponses(root, get, ["200", "400", "404"]);
        }

        var create = paths.GetProperty("/api/v1/comments").GetProperty("post");
        var createSchema = ResolveOpenApiSchema(root, create.GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var body = ResolveOpenApiSchema(root, createSchema.GetProperty("properties").GetProperty("body"));
        Assert.Equal(1, body.GetProperty("minLength").GetInt32());
        Assert.Equal(2000, body.GetProperty("maxLength").GetInt32());
        AssertCommentResponses(root, create, ["201", "400", "401", "403", "404", "409", "422", "429"]);

        var commentPath = paths.GetProperty("/api/v1/comments/{commentId}");
        AssertCommentResponses(root, commentPath.GetProperty("patch"), ["200", "400", "401", "403", "404", "409", "429"]);
        AssertCommentResponses(root, commentPath.GetProperty("delete"), ["200", "400", "401", "403", "404", "409", "429"]);

        var adminList = paths.GetProperty("/api/v1/admin/comments").GetProperty("get");
        AssertCommentResponses(root, adminList, ["200", "400", "401", "403"]);
        var adminPage = ResolveResponseSchema(root, adminList, "200");
        var adminItem = ResolveOpenApiSchema(root,
            ResolveOpenApiSchema(root, adminPage.GetProperty("properties").GetProperty("items"))
                .GetProperty("items"));
        foreach (var property in new[] { "targetTitle", "targetSlug", "parentBody", "parentAuthorDisplayName" })
            Assert.True(adminItem.GetProperty("properties").TryGetProperty(property, out _), $"Missing Admin context property {property}.");
        foreach (var action in new[] { "hide", "delete", "restore" })
        {
            var operation = paths.GetProperty($"/api/v1/admin/comments/{{commentId}}/{action}").GetProperty("post");
            AssertCommentResponses(root, operation, ["200", "400", "401", "403", "404", "409", "429"]);
        }

        static void AssertCommentResponses(JsonElement documentRoot, JsonElement operation, string[] statuses)
        {
            foreach (var status in statuses)
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), $"Missing response {status}.");
                if (status is "200" or "201") continue;
                var error = ResolveResponseSchema(documentRoot, operation, status);
                foreach (var property in new[] { "errorCode", "message", "traceId" })
                    Assert.True(error.GetProperty("properties").TryGetProperty(property, out _));
            }
        }
    }

    [Fact]
    public async Task Comment_http_validation_and_unavailable_targets_fail_closed_with_standard_errors()
    {
        var (comicId, _) = await SeedPublicRatingComicAsync(_factory.Services, "in02-negative");
        _factory.TokenEndpoint.SetProfile($"in02-negative-{comicId:N}", "IN02 Negative", null, null);
        using var user = CreateClient();
        await CompleteLoginAsync(user, await BeginLoginAsync(user, "/"));
        var csrf = Assert.Single((await user.GetAsync("/api/v1/me")).Headers.GetValues("X-CSRF-TOKEN"));

        async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string json)
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            return await user.SendAsync(request);
        }

        foreach (var json in new[]
                 {
                     JsonSerializer.Serialize(new { comicId }),
                     JsonSerializer.Serialize(new { comicId, body = (string?)null }),
                     JsonSerializer.Serialize(new { comicId, body = "   " }),
                     JsonSerializer.Serialize(new { comicId, body = new string('x', Comment.MaximumBodyLength + 1) }),
                     "{",
                 })
            await AssertStandardizedErrorAsync(
                await SendJsonAsync(HttpMethod.Post, "/api/v1/comments", json),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");

        var created = await SendJsonAsync(
            HttpMethod.Post,
            "/api/v1/comments",
            JsonSerializer.Serialize(new { comicId, body = "Owner mutation" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var commentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        foreach (var json in new[] { "{}", "{\"expectedVersion\":0}", "{\"expectedVersion\":-1}", "{" })
            await AssertStandardizedErrorAsync(
                await SendJsonAsync(HttpMethod.Delete, $"/api/v1/comments/{commentId}", json),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");

        await HideComicAsync(_factory.Services, comicId);
        await AssertStandardizedErrorAsync(
            await SendJsonAsync(
                HttpMethod.Post,
                "/api/v1/comments",
                JsonSerializer.Serialize(new { comicId, body = "Hidden target" })),
            HttpStatusCode.NotFound,
            "RESOURCE_NOT_FOUND");

        var (chapterComicId, _) = await SeedPublicRatingComicAsync(_factory.Services, "in02-chapter-negative");
        Guid chapterId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            chapterId = await context.Chapters.Where(chapter => chapter.ComicId == chapterComicId).Select(chapter => chapter.Id).SingleAsync();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE catalog.chapter_pages SET asset_status = {ChapterPageAssetStatuses.Pending}, width = 0, height = 0 WHERE chapter_id = {chapterId}");
        }
        await AssertStandardizedErrorAsync(
            await SendJsonAsync(
                HttpMethod.Post,
                "/api/v1/comments",
                JsonSerializer.Serialize(new { chapterId, body = "Not ready" })),
            HttpStatusCode.NotFound,
            "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task A02_admin_finance_reads_enforce_authorization_and_validation()
    {
        using var anonymous = CreateClient();
        foreach (var path in new[] { "/api/v1/admin/payments", "/api/v1/admin/ledger", "/api/v1/admin/refunds" })
            await AssertStandardizedErrorAsync(await anonymous.GetAsync(path),
                HttpStatusCode.Unauthorized, "AUTH_REQUIRED");

        await using (var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(), _securityDirectory,
            useInsufficientRoleTestAuthentication: true))
        using (var user = CreateClient(factory))
        {
            await AssertStandardizedErrorAsync(await user.GetAsync("/api/v1/admin/payments"),
                HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        var (admin, _) = await CreateAdminClientAsync($"a02-admin-{Guid.NewGuid():N}");
        using (admin)
        {
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/payments?status=Unknown"),
                HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/payments?status=1"),
                HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            Assert.Equal(HttpStatusCode.OK,
                (await admin.GetAsync("/api/v1/admin/payments?status=Pending&type=TopUp")).StatusCode);
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/refunds?status=1"),
                HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            Assert.Equal(HttpStatusCode.OK,
                (await admin.GetAsync("/api/v1/admin/ledger?operationType=RefundReserve")).StatusCode);
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/ledger?pageSize=101"),
                HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            await AssertStandardizedErrorAsync(
                await admin.GetAsync($"/api/v1/admin/refunds/{Guid.NewGuid()}"),
                HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND");
        }
    }

    [Fact]
    public async Task A02_openapi_declares_finance_pagination_safe_schemas_and_standard_errors()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        foreach (var path in new[] { "/api/v1/admin/payments", "/api/v1/admin/ledger", "/api/v1/admin/refunds" })
        {
            var operation = root.GetProperty("paths").GetProperty(path).GetProperty("get");
            var pageSize = FindOpenApiParameter(operation, "pageSize").GetProperty("schema");
            Assert.Equal(1, pageSize.GetProperty("minimum").GetInt32());
            Assert.Equal(100, pageSize.GetProperty("maximum").GetInt32());
            foreach (var status in new[] { "200", "400", "401", "403" })
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), $"{path}:{status}");
                if (status == "200") continue;
                var error = ResolveResponseSchema(root, operation, status);
                foreach (var property in new[] { "errorCode", "message", "traceId" })
                    Assert.True(error.GetProperty("properties").TryGetProperty(property, out _), $"{path}:{status}:{property}");
            }
        }

        foreach (var path in new[] { "/api/v1/admin/payments/{paymentId}", "/api/v1/admin/refunds/{refundId}" })
        {
            var operation = root.GetProperty("paths").GetProperty(path).GetProperty("get");
            foreach (var status in new[] { "200", "400", "401", "403", "404" })
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), $"{path}:{status}");
        }

        var refundDetail = ResolveResponseSchema(root,
            root.GetProperty("paths").GetProperty("/api/v1/admin/refunds/{refundId}").GetProperty("get"), "200");
        var recipient = ResolveOpenApiSchema(
            root,
            refundDetail.GetProperty("properties").GetProperty("recipient"));
        var serialized = recipient.GetRawText();
        Assert.Contains("accountNumberMasked", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("accountNumberEncrypted", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("requestFingerprint", serialized, StringComparison.Ordinal);

        Assert.Equal(
            new[] { "Pending", "Expired", "Succeeded" },
            GetOpenApiEnumValues(root, FindOpenApiParameter(
                root.GetProperty("paths").GetProperty("/api/v1/admin/payments").GetProperty("get"),
                "status").GetProperty("schema")));
        Assert.Equal(
            new[] { "TopUp", "VipPurchase" },
            GetOpenApiEnumValues(root, FindOpenApiParameter(
                root.GetProperty("paths").GetProperty("/api/v1/admin/payments").GetProperty("get"),
                "type").GetProperty("schema")));
        Assert.Equal(
            new[] { "TopUpCredit", "ChapterUnlock", "MissionReward", "RefundReserve", "RefundComplete", "RefundRelease" },
            GetOpenApiEnumValues(root, FindOpenApiParameter(
                root.GetProperty("paths").GetProperty("/api/v1/admin/ledger").GetProperty("get"),
                "operationType").GetProperty("schema")));
        Assert.Equal(
            new[] { "Requested", "UnderReview", "RecoveryReserved", "TransferPending", "Completed", "Rejected", "NeedsReconciliation", "ReservationReleased" },
            GetOpenApiEnumValues(root, FindOpenApiParameter(
                root.GetProperty("paths").GetProperty("/api/v1/admin/refunds").GetProperty("get"),
                "status").GetProperty("schema")));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.Equal(new[] { "Pending", "Expired", "Succeeded" },
            GetOpenApiEnumValues(root, schemas.GetProperty("AdminPaymentListItem")
                .GetProperty("properties").GetProperty("orderStatus")));
        Assert.Equal(new[] { "TopUp", "VipPurchase" },
            GetOpenApiEnumValues(root, schemas.GetProperty("AdminPaymentListItem")
                .GetProperty("properties").GetProperty("orderType")));
        Assert.Equal(new[] { "TopUpCredit", "ChapterUnlock", "MissionReward", "RefundReserve", "RefundComplete", "RefundRelease" },
            GetOpenApiEnumValues(root, schemas.GetProperty("AdminLedgerItem")
                .GetProperty("properties").GetProperty("operationType")));
        Assert.Equal(new[] { "Requested", "UnderReview", "RecoveryReserved", "TransferPending", "Completed", "Rejected", "NeedsReconciliation", "ReservationReleased" },
            GetOpenApiEnumValues(root, schemas.GetProperty("AdminRefundListItem")
                .GetProperty("properties").GetProperty("status")));
    }

    [Fact]
    public async Task A03_admin_users_enforces_auth_csrf_version_and_revokes_disabled_session()
    {
        var subject = $"a03-user-{Guid.NewGuid():N}";
        _factory.TokenEndpoint.SetProfile(subject, "A03 Reader", "a03-reader@example.test", null);
        using var reader = CreateClient();
        await CompleteLoginAsync(reader, await BeginLoginAsync(reader, "/"));
        var readerProfile = await (await reader.GetAsync("/api/v1/me"))
            .Content.ReadFromJsonAsync<MeResponse>();

        var (admin, csrf) = await CreateAdminClientAsync($"a03-admin-{Guid.NewGuid():N}");
        using (admin)
        {
            using var anonymous = CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.GetAsync("/api/v1/admin/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await reader.GetAsync("/api/v1/admin/users")).StatusCode);

            var invalidFilter = await admin.GetAsync("/api/v1/admin/users?status=1");
            Assert.Equal(HttpStatusCode.BadRequest, invalidFilter.StatusCode);
            Assert.Equal("VALIDATION_FAILED",
                (await invalidFilter.Content.ReadFromJsonAsync<ApiErrorResponse>())!.ErrorCode);

            var page = await (await admin.GetAsync("/api/v1/admin/users?search=a03-reader@example.test&status=Active"))
                .Content.ReadFromJsonAsync<AdminUserPage>();
            var target = Assert.Single(page!.Items);
            Assert.Equal(readerProfile!.Id, target.Id);

            var missingCsrf = await admin.PostAsJsonAsync(
                $"/api/v1/admin/users/{target.Id}/disable",
                new { expectedVersion = target.Version });
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            Assert.Equal("VALIDATION_FAILED", (await missingCsrf.Content.ReadFromJsonAsync<ApiErrorResponse>())!.ErrorCode);

            var disable = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/disable")
            {
                Content = JsonContent.Create(new { expectedVersion = target.Version }),
            };
            disable.Headers.Add("X-CSRF-TOKEN", csrf);
            var disabled = await admin.SendAsync(disable);
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            var detail = await disabled.Content.ReadFromJsonAsync<AdminUserDetail>();
            Assert.Equal(UserStatuses.Disabled, detail!.User.Status);

            var stale = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/enable")
            {
                Content = JsonContent.Create(new { expectedVersion = target.Version }),
            };
            stale.Headers.Add("X-CSRF-TOKEN", csrf);
            var conflict = await admin.SendAsync(stale);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("CONCURRENCY_CONFLICT", (await conflict.Content.ReadFromJsonAsync<ApiErrorResponse>())!.ErrorCode);
        }

        var rejectedSession = await reader.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, rejectedSession.StatusCode);
    }

    [Fact]
    public async Task A03_paged_queries_reject_unsupported_offsets_without_returning_500()
    {
        var (admin, _) = await CreateAdminClientAsync($"a03-pagination-{Guid.NewGuid():N}");
        using (admin)
        {
            foreach (var path in new[]
            {
                "/api/v1/admin/users?page=1&pageSize=30",
                "/api/v1/admin/users?page=100000&pageSize=100",
                "/api/v1/admin/users?page=21474837&pageSize=100",
                $"/api/v1/admin/users?page={int.MaxValue}&pageSize=1",
                "/api/v1/admin/mission-rewards?page=1&pageSize=30",
                "/api/v1/admin/mission-rewards?page=21474837&pageSize=100",
                $"/api/v1/admin/mission-rewards?page={int.MaxValue}&pageSize=1",
            })
            {
                using var response = await admin.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            foreach (var path in new[]
            {
                $"/api/v1/admin/users?page={int.MaxValue}&pageSize=100",
                $"/api/v1/admin/mission-rewards?page={int.MaxValue}&pageSize=100",
            })
            {
                using var response = await admin.GetAsync(path);
                await AssertStandardizedErrorAsync(
                    response,
                    HttpStatusCode.BadRequest,
                    "VALIDATION_FAILED");
            }
        }
    }

    [Fact]
    public async Task A03_user_mutation_rate_limit_is_twenty_per_authenticated_admin()
    {
        var subject = $"a03-rate-user-{Guid.NewGuid():N}";
        _factory.TokenEndpoint.SetProfile(subject, "A03 Rate Reader", null, null);
        using var reader = CreateClient();
        await CompleteLoginAsync(reader, await BeginLoginAsync(reader, "/"));
        var readerProfile = await (await reader.GetAsync("/api/v1/me"))
            .Content.ReadFromJsonAsync<MeResponse>();

        var (adminA, csrfA) = await CreateAdminClientAsync($"a03-rate-admin-a-{Guid.NewGuid():N}");
        var (adminB, csrfB) = await CreateAdminClientAsync($"a03-rate-admin-b-{Guid.NewGuid():N}");
        using (adminA)
        using (adminB)
        {
            var version = (await (await adminA.GetAsync($"/api/v1/admin/users/{readerProfile!.Id}"))
                .Content.ReadFromJsonAsync<AdminUserDetail>())!.User.Version;

            for (var attempt = 1; attempt <= 20; attempt++)
            {
                var action = attempt % 2 == 1 ? "disable" : "enable";
                using var response = await SendAdminJsonAsync(
                    adminA,
                    HttpMethod.Post,
                    $"/api/v1/admin/users/{readerProfile.Id}/{action}",
                    new { expectedVersion = version },
                    csrfA);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                version = (await response.Content.ReadFromJsonAsync<AdminUserDetail>())!.User.Version;
            }

            using (var limited = await SendAdminJsonAsync(
                adminA,
                HttpMethod.Post,
                $"/api/v1/admin/users/{readerProfile.Id}/disable",
                new { expectedVersion = version },
                csrfA))
            {
                await AssertStandardizedErrorAsync(
                    limited,
                    HttpStatusCode.TooManyRequests,
                    "RATE_LIMITED");
            }

            using var independent = await SendAdminJsonAsync(
                adminB,
                HttpMethod.Post,
                $"/api/v1/admin/users/{readerProfile.Id}/disable",
                new { expectedVersion = version },
                csrfB);
            Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
        }
    }

    [Fact]
    public async Task A03_vip_plan_mutation_rate_limit_is_twenty_per_authenticated_admin()
    {
        var (adminA, csrfA) = await CreateAdminClientAsync($"a03-vip-rate-a-{Guid.NewGuid():N}");
        var (adminB, csrfB) = await CreateAdminClientAsync($"a03-vip-rate-b-{Guid.NewGuid():N}");
        using (adminA)
        using (adminB)
        {
            var plan = await (await adminA.GetAsync("/api/v1/admin/vip-plan"))
                .Content.ReadFromJsonAsync<VipPlanResponse>();
            Assert.NotNull(plan);

            for (var attempt = 1; attempt <= 20; attempt++)
            {
                using var response = await SendAdminJsonAsync(
                    adminA,
                    HttpMethod.Patch,
                    "/api/v1/admin/vip-plan",
                    new { priceVnd = plan!.PriceVnd + 1, expectedVersion = plan.Version },
                    csrfA);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                plan = await response.Content.ReadFromJsonAsync<VipPlanResponse>();
            }

            using (var limited = await SendAdminJsonAsync(
                adminA,
                HttpMethod.Patch,
                "/api/v1/admin/vip-plan",
                new { priceVnd = plan!.PriceVnd + 1, expectedVersion = plan.Version },
                csrfA))
            {
                await AssertStandardizedErrorAsync(
                    limited,
                    HttpStatusCode.TooManyRequests,
                    "RATE_LIMITED");
            }

            using var independent = await SendAdminJsonAsync(
                adminB,
                HttpMethod.Patch,
                "/api/v1/admin/vip-plan",
                new { priceVnd = plan!.PriceVnd + 1, expectedVersion = plan.Version },
                csrfB);
            Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
        }
    }

    [Fact]
    public async Task A03_OpenApi_contains_closed_user_contract_and_admin_errors()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        var list = paths.GetProperty("/api/v1/admin/users").GetProperty("get");
        Assert.Equal(
            new[] { "Active", "Disabled" },
            GetOpenApiEnumValues(root, FindOpenApiParameter(list, "status").GetProperty("schema")));
        foreach (var status in new[] { "200", "400", "401", "403" })
        {
            Assert.True(list.GetProperty("responses").TryGetProperty(status, out _));
        }

        var schema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("AdminUserListItem").GetProperty("properties");
        Assert.Equal(new[] { "User", "Admin" }, GetOpenApiEnumValues(root, schema.GetProperty("role")));
        Assert.Equal(new[] { "Active", "Disabled" }, GetOpenApiEnumValues(root, schema.GetProperty("status")));
        var paymentSchema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("AdminUserPaymentSummary").GetProperty("properties");
        Assert.Equal(new[] { "Pending", "Expired", "Succeeded" },
            GetOpenApiEnumValues(root, paymentSchema.GetProperty("status")));
        Assert.Equal(new[] { "TopUp", "VipPurchase" },
            GetOpenApiEnumValues(root, paymentSchema.GetProperty("orderType")));

        foreach (var action in new[] { "disable", "enable" })
        {
            var operation = paths.GetProperty($"/api/v1/admin/users/{{userId}}/{action}").GetProperty("post");
            foreach (var status in new[] { "200", "400", "401", "403", "404", "409", "429" })
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
            }
        }

        var rewards = paths.GetProperty("/api/v1/admin/mission-rewards").GetProperty("get");
        Assert.Equal(
            new[] { "Granted", "DeniedDailyCap" },
            GetOpenApiEnumValues(root, FindOpenApiParameter(rewards, "outcome").GetProperty("schema")));
        foreach (var status in new[] { "200", "400", "401", "403" })
        {
            Assert.True(rewards.GetProperty("responses").TryGetProperty(status, out _));
        }
        var rewardSchema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("AdminMissionRewardItem").GetProperty("properties");
        Assert.Equal(
            new[] { "Granted", "DeniedDailyCap" },
            GetOpenApiEnumValues(root, rewardSchema.GetProperty("outcome")));

        var errorSchema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("ApiErrorResponse").GetProperty("properties");
        foreach (var property in new[] { "errorCode", "message", "traceId" })
        {
            Assert.True(errorSchema.TryGetProperty(property, out _));
        }
    }

    [Fact]
    public async Task A04_reporting_requires_Admin_and_validates_Vietnam_ranges_and_pagination()
    {
        using var anonymous = CreateClient();
        await AssertStandardizedErrorAsync(
            await anonymous.GetAsync("/api/v1/admin/dashboard"),
            HttpStatusCode.Unauthorized,
            "AUTH_REQUIRED");
        await AssertStandardizedErrorAsync(
            await anonymous.GetAsync("/api/v1/admin/audit"),
            HttpStatusCode.Unauthorized,
            "AUTH_REQUIRED");

        await using (var factory = new AuthenticationApplicationFactory(
            _postgres.GetConnectionString(), _securityDirectory,
            useInsufficientRoleTestAuthentication: true))
        using (var user = CreateClient(factory))
        {
            await AssertStandardizedErrorAsync(
                await user.GetAsync("/api/v1/admin/dashboard"),
                HttpStatusCode.Forbidden,
                "FORBIDDEN");
        }

        var (admin, _) = await CreateAdminClientAsync($"a04-admin-{Guid.NewGuid():N}");
        using (admin)
        {
            var dashboard = await admin.GetAsync(
                "/api/v1/admin/dashboard?from=2026-10-01&to=2026-10-02");
            Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
            Assert.Equal("no-store", dashboard.Headers.CacheControl?.ToString());
            using var body = JsonDocument.Parse(await dashboard.Content.ReadAsStringAsync());
            foreach (var property in new[]
                     {
                         "range", "revenue", "refunds", "coinsIssued", "reading", "missions", "dailySeries",
                     })
                Assert.True(body.RootElement.TryGetProperty(property, out _), property);

            var audit = await admin.GetAsync(
                "/api/v1/admin/audit?from=2026-10-01&to=2026-10-02&page=1&pageSize=30");
            Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
            Assert.Equal("no-store", audit.Headers.CacheControl?.ToString());
            using var auditBody = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
            foreach (var property in new[] { "items", "page", "pageSize", "totalItems", "totalPages" })
                Assert.True(auditBody.RootElement.TryGetProperty(property, out _), property);

            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/dashboard?from=2026-10-02&to=2026-10-01"),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/dashboard?from=2024-01-01&to=2025-01-01"),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/audit?pageSize=101"),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
            await AssertStandardizedErrorAsync(
                await admin.GetAsync("/api/v1/admin/audit?page=2147483647&pageSize=100"),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
            await AssertStandardizedErrorAsync(
                await admin.GetAsync($"/api/v1/admin/audit?actorId={Guid.Empty}"),
                HttpStatusCode.BadRequest,
                "VALIDATION_FAILED");
        }
    }

    [Fact]
    public async Task A04_OpenApi_declares_reporting_filters_pagination_and_standard_errors()
    {
        using var client = CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        foreach (var path in new[] { "/api/v1/admin/dashboard", "/api/v1/admin/audit" })
        {
            var operation = root.GetProperty("paths").GetProperty(path).GetProperty("get");
            foreach (var status in new[] { "200", "400", "401", "403" })
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), $"{path}:{status}");
                if (status == "200") continue;
                var error = ResolveResponseSchema(root, operation, status);
                foreach (var property in new[] { "errorCode", "message", "traceId" })
                    Assert.True(error.GetProperty("properties").TryGetProperty(property, out _), $"{path}:{status}:{property}");
            }
            foreach (var dateParameter in new[] { "from", "to" })
                Assert.Equal("date", FindOpenApiParameter(operation, dateParameter)
                    .GetProperty("schema").GetProperty("format").GetString());
        }

        var audit = root.GetProperty("paths").GetProperty("/api/v1/admin/audit").GetProperty("get");
        var page = FindOpenApiParameter(audit, "page");
        Assert.Contains("(page - 1) * pageSize", page.GetProperty("description").GetString());
        Assert.Contains("400 VALIDATION_FAILED", page.GetProperty("description").GetString());
        var pageSize = FindOpenApiParameter(audit, "pageSize").GetProperty("schema");
        Assert.Equal(1, pageSize.GetProperty("minimum").GetInt32());
        Assert.Equal(100, pageSize.GetProperty("maximum").GetInt32());
        foreach (var filter in new[] { "action", "targetType" })
            Assert.Equal(AdministrationReportingHandler.MaximumFilterLength,
                FindOpenApiParameter(audit, filter).GetProperty("schema").GetProperty("maxLength").GetInt32());

        var item = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("AdminAuditItem").GetProperty("properties");
        Assert.True(item.TryGetProperty("redactedChanges", out _));
        Assert.False(item.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task A04_audit_read_boundary_redacts_adversarial_legacy_payloads()
    {
        var subject = $"a04-redaction-{Guid.NewGuid():N}";
        var (admin, _) = await CreateAdminClientAsync(subject);
        using (admin)
        {
            Guid actorId;
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                actorId = await context.GoogleIdentities
                    .Where(identity => identity.GoogleSubject == subject)
                    .Select(identity => identity.UserId)
                    .SingleAsync();
                var auditId = Guid.NewGuid();
                var targetId = Guid.NewGuid();
                var payload =
                    "{\"status\":\"Succeeded\",\"accountNumber\":\"0123456789\"," +
                    "\"CipherText\":\"protected-cipher\",\"Password\":\"password-value\"," +
                    "\"nested\":{\"Api-Key\":\"secret-key\"," +
                    "\"AccessToken\":\"access-token-value\",\"safe\":true}," +
                    "\"items\":[{\"recipientAccountHolder\":\"NGUYEN VAN A\"," +
                    "\"providerPayload\":\"raw-provider-data\"," +
                    "\"rawFinancialPayload\":\"raw-financial-data\",\"code\":\"OK\"}]}";
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO administration.audit_logs
                        (id, actor_id, action, target_type, target_id,
                         correlation_id, occurred_at, redacted_changes)
                    VALUES ({auditId}, {actorId}, {"A04.Adversarial"}, {"PaymentOrder"},
                            {targetId}, {"a04-adversarial"}, {DateTimeOffset.UtcNow},
                            CAST({payload} AS jsonb));
                    """);
            }

            var response = await admin.GetAsync(
                "/api/v1/admin/audit?action=A04.Adversarial&page=1&pageSize=30");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var changes = body.RootElement.GetProperty("items")[0]
                .GetProperty("redactedChanges").GetString();
            using var safe = JsonDocument.Parse(changes!);
            Assert.Equal("Succeeded", safe.RootElement.GetProperty("status").GetString());
            Assert.True(safe.RootElement.GetProperty("nested").GetProperty("safe").GetBoolean());
            Assert.Equal("OK", safe.RootElement.GetProperty("items")[0]
                .GetProperty("code").GetString());
            foreach (var forbidden in new[]
                     {
                         "0123456789", "NGUYEN VAN A", "secret-key", "raw-provider-data",
                         "protected-cipher", "password-value", "access-token-value",
                         "raw-financial-data", "accountNumber", "recipientAccountHolder",
                         "Api-Key", "providerPayload", "CipherText", "Password", "AccessToken",
                         "rawFinancialPayload",
                     })
                Assert.DoesNotContain(forbidden, changes, StringComparison.OrdinalIgnoreCase);
        }
    }

    private HttpClient CreateClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

    private async Task<(HttpClient Client, string Csrf)> CreateAdminClientAsync(string subject)
    {
        _factory.TokenEndpoint.SetProfile(subject, "Mission Admin", null, null);
        using (var userClient = CreateClient())
        {
            await CompleteLoginAsync(
                userClient,
                await BeginLoginAsync(userClient, "/admin/missions"));
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Users
                .Where(user => context.GoogleIdentities.Any(identity =>
                    identity.UserId == user.Id && identity.GoogleSubject == subject))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(user => user.Role, IdentityRoles.Admin)
                    .SetProperty(user => user.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(user => user.Version, user => user.Version + 1));
        }

        var adminClient = CreateClient();
        await CompleteLoginAsync(
            adminClient,
            await BeginLoginAsync(adminClient, "/admin/missions"));
        var profile = await adminClient.GetAsync("/api/v1/me");
        return (adminClient, Assert.Single(profile.Headers.GetValues("X-CSRF-TOKEN")));
    }

    private static async Task<HttpResponseMessage> SendAdminJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        string csrf)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage PageUploadRequest(
        Guid chapterId, long expectedVersion, string csrf, string fileName, byte[] bytes,
        string contentType = "image/png")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), "expectedVersion" },
            { new ByteArrayContent(bytes) { Headers = { ContentType = new(contentType) } }, "files", fileName },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/chapters/{chapterId}/pages")
        { Content = content };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return request;
    }

    private static HttpRequestMessage CreateMissionRequest(string code, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/missions")
        {
            Content = JsonContent.Create(new
            {
                code,
                name = code,
                description = string.Empty,
                actionType = "ChapterReadQualified",
                cycleType = "Daily",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return request;
    }

    private static HttpClient CreateClient(AuthenticationApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

    private static async Task<(Guid ComicId, string Slug)> SeedPublicRatingComicAsync(
        IServiceProvider services,
        string prefix)
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var slug = $"{prefix}-{comicId:N}";
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var comic = Comic.Create(
            comicId,
            slug,
            "IN01 Race Comic",
            string.Empty,
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            now);
        var chapter = Chapter.Create(
            chapterId,
            comicId,
            "Chapter 1",
            1,
            0,
            null,
            now);
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            1,
            $"in01-race/{comicId:N}/1",
            $"sha256:{comicId:N}{comicId:N}",
            now);
        page.MarkReady(1200, 1800);
        chapter.Publish(true, 1, now.AddMinutes(1));
        comic.Publish(true, 1, now.AddMinutes(2));
        context.AddRange(comic, chapter, page);
        await context.SaveChangesAsync();
        return (comicId, slug);
    }

    private static async Task HideComicAsync(IServiceProvider services, Guid comicId)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hidden = ComicPublicationStatuses.Hidden;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.comics SET publication_status = {hidden} WHERE id = {comicId}");
    }

    private static async Task AssertStandardizedErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedErrorCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedErrorCode, body.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    private static HttpRequestMessage TopUpRequest(
        string idempotencyKey,
        long amountVnd,
        string? csrfToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/topups")
        {
            Content = JsonContent.Create(new { amountVnd }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        return request;
    }

    private static RefundSecuritySettings RefundSecuritySettingsFor(
        string currentFingerprintVersion,
        string currentProtectionVersion,
        IReadOnlyDictionary<string, byte[]> keys) => new()
        {
            CurrentFingerprintVersion = currentFingerprintVersion,
            CurrentProtectionVersion = currentProtectionVersion,
            FingerprintKeys = keys,
        };

    private static ServiceProvider CreateRefundDataProtectionServices(
        DirectoryInfo keyDirectory)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(keyDirectory)
            .SetApplicationName("ComicManagement.RF01.Tests")
            .DisableAutomaticKeyGeneration();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static HttpRequestMessage RefundRequestMessage(
        Guid paymentOrderId,
        string idempotencyKey,
        string? csrfToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/refunds")
        {
            Content = JsonContent.Create(new
            {
                paymentOrderId,
                reason = "duplicate transfer",
                recipient = new
                {
                    bankCode = "VCB",
                    accountNumber = "0123456789",
                    accountHolder = "NGUYEN VAN A",
                },
            }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        return request;
    }

    private static HttpRequestMessage UnlockRequest(
        Guid chapterId,
        Guid quoteId,
        string? idempotencyKey,
        string? csrfToken = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/chapters/{chapterId}/unlock")
        {
            Content = JsonContent.Create(new { quoteId }),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        return request;
    }

    private static HttpRequestMessage VipCheckoutRequest(
        string idempotencyKey,
        string? csrfToken = null,
        Guid? planId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/membership/checkout")
        {
            Content = JsonContent.Create(new { planId = planId ?? VipPlan.InitialPlanId }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        return request;
    }

    private AuthenticationApplicationFactory CreateSePayFactory(
        int sePayPermitLimitPerMinute = 120) => new(
            _postgres.GetConnectionString(),
            _securityDirectory,
            sePayWebhookEnabled: true,
            sePayPermitLimitPerMinute: sePayPermitLimitPerMinute);

    private static HttpRequestMessage SePayRequest(
        string providerTransactionId,
        long amountVnd,
        DateTimeOffset? signedAt = null,
        string paymentCode = "ZENO-0123456789abcdef")
    {
        var timestamp = (signedAt ?? DateTimeOffset.UtcNow)
            .ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payload = JsonSerializer.Serialize(new
        {
            id = long.Parse(providerTransactionId, System.Globalization.CultureInfo.InvariantCulture),
            gateway = "MBBank",
            transactionDate = "2026-10-16 10:30:00",
            accountNumber = SePayAccountNumber,
            subAccount = string.Empty,
            code = paymentCode,
            content = $"{paymentCode} chuyen tien",
            transferType = "in",
            description = "NGUYEN VAN A chuyen tien",
            transferAmount = amountVnd,
            accumulated = 5_000_000,
            referenceCode = "FT24012345678",
        });
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SePayWebhookSecret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/webhooks/sepay/ipn")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            SePayWebhookHeaders.Timestamp,
            timestamp);
        request.Headers.TryAddWithoutValidation(
            SePayWebhookHeaders.Signature,
            $"sha256={Convert.ToHexString(signature).ToLowerInvariant()}");
        return request;
    }

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
        bool forceProfileConflicts = false,
        int readingProgressPermitLimit = 60,
        int topUpOrderTtlMinutes = 30,
        int vipOrderTtlMinutes = 30,
        bool useInsufficientRoleTestAuthentication = false,
        bool sePayWebhookEnabled = false,
        int sePayPermitLimitPerMinute = 120,
        IComicRatingReadObserver? comicRatingReadObserver = null,
        IPublicComicDetailReadObserver? comicDetailReadObserver = null) : WebApplicationFactory<Program>
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
                .UseSetting("Cloudinary:CloudName", "test-cloud")
                .UseSetting("Cloudinary:ApiKey", "test-key")
                .UseSetting("Cloudinary:ApiSecret", "test-secret")
                .UseSetting("Cloudinary:MaximumChapterPageBytes", "10485760")
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
                .UseSetting("Billing:RefundSecurity:CurrentFingerprintVersion", "v1")
                .UseSetting(
                    "Billing:RefundSecurity:FingerprintKeys:v1",
                    "cmYwMS10ZXN0LWZpbmdlcnByaW50LWtleS12MS0zMiE=")
                .UseSetting("Billing:RefundSecurity:CurrentProtectionVersion", "v1")
                .UseSetting(
                    "Reading:ProgressRateLimit:PermitLimit",
                    readingProgressPermitLimit.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .UseSetting(
                    "Billing:TopUpOrderTtlMinutes",
                    topUpOrderTtlMinutes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .UseSetting(
                    "Membership:VipOrderTtlMinutes",
                    vipOrderTtlMinutes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .UseSetting(
                    "Billing:SePayWebhook:Enabled",
                    sePayWebhookEnabled.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .UseSetting("Billing:SePayWebhook:Secret", SePayWebhookSecret)
                .UseSetting(
                    "Billing:SePayWebhook:ExpectedAccountNumber",
                    SePayAccountNumber)
                .UseSetting("Billing:SePayWebhook:TimestampToleranceSeconds", "300")
                .UseSetting("Billing:SePayWebhook:MaximumBodyBytes", "65536")
                .UseSetting(
                    "Billing:SePayWebhook:PermitLimitPerMinute",
                    sePayPermitLimitPerMinute.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
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

                services.RemoveAll<IChapterPageContentProvider>();
                services.AddSingleton<IChapterPageContentProvider, FakeChapterPageContentProvider>();
                services.RemoveAll<IChapterPageAssetStore>();
                services.AddSingleton<IChapterPageAssetStore, FakeAdminChapterPageAssetStore>();

                if (useInsufficientRoleTestAuthentication)
                {
                    services.AddAuthentication(options =>
                        {
                            options.DefaultAuthenticateScheme =
                                InsufficientRoleAuthenticationHandler.SchemeName;
                            options.DefaultChallengeScheme =
                                InsufficientRoleAuthenticationHandler.SchemeName;
                            options.DefaultForbidScheme =
                                InsufficientRoleAuthenticationHandler.SchemeName;
                        })
                        .AddScheme<AuthenticationSchemeOptions,
                            InsufficientRoleAuthenticationHandler>(
                            InsufficientRoleAuthenticationHandler.SchemeName,
                            _ => { });
                }

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

                if (comicRatingReadObserver is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(comicRatingReadObserver));
                }

                if (comicDetailReadObserver is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(comicDetailReadObserver));
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TokenEndpoint.Dispose();
        }
    }

    private sealed class BlockingRatingReadObserver : IComicRatingReadObserver
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AfterPublicVisibilityReadAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class BlockingComicDetailReadObserver : IPublicComicDetailReadObserver
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AfterCatalogReadAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class InsufficientRoleAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) :
        AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "InsufficientRole";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D"))],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            Response.ContentType = "application/problem+json";
            return Response.WriteAsJsonAsync(new
            {
                type = "https://docs.example.test/errors/FORBIDDEN",
                title = "Forbidden",
                status = StatusCodes.Status403Forbidden,
                errorCode = "FORBIDDEN",
                message = "The current user does not have permission to perform this action.",
                traceId = Context.TraceIdentifier,
            });
        }
    }

    private sealed class FakeChapterPageContentProvider : IChapterPageContentProvider
    {
        public Task<ChapterPageContent?> GetAsync(
            string assetKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ChapterPageContent?>(
                new ChapterPageContent([1, 2, 3], "image/webp"));
    }

    private sealed class FakeAdminChapterPageAssetStore : IChapterPageAssetStore
    {
        public void ValidateUpload(ChapterPageAssetUpload upload) { }
        public string GetPublicId(Guid chapterId, Guid pageId) => $"test-pages/{chapterId:N}/{pageId:N}";
        public Task<ChapterPageAsset> UploadAsync(ChapterPageAssetUpload upload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChapterPageAsset(GetPublicId(upload.ChapterId, upload.PageId), 800, 1200));
        public Task DeleteAsync(string publicId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

        public void AddLoginEvent(UserLoggedIn loginEvent)
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
