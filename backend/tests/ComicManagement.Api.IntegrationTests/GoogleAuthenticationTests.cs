using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ComicManagement.Catalog.Browsing;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Media;
using ComicManagement.Catalog.Publishing;
using ComicManagement.Persistence;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Profiles;
using ComicManagement.Identity.Users;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Library;
using ComicManagement.Reading.Sessions;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Workflows.Reading;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Queries;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Events;
using ComicManagement.Membership.Events;
using ComicManagement.Membership.Memberships;
using ComicManagement.Membership.Plans;
using ComicManagement.Membership.Subscriptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
    public async Task Production_composition_registers_the_chapter_page_upload_slice()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<UploadChapterPageHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChapterPageAssetStore>());
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

        using var request = UnlockRequest(chapterId, quoteId, "u02-http", csrf);
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
                 })
        {
            Assert.True(detailSchema.GetProperty("properties").TryGetProperty(property, out _));
        }

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

    private HttpClient CreateClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

    private static HttpClient CreateClient(AuthenticationApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

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
        int sePayPermitLimitPerMinute = 120) : WebApplicationFactory<Program>
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
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TokenEndpoint.Dispose();
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
