using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Administration.Auditing;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Media;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Identity.Users;
using ComicManagement.Persistence;
using ComicManagement.Reading.Access;
using ComicManagement.Wallet.Lots;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

[assembly: WebApplicationFactoryContentRoot(
    "ComicManagement.Api, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
    "../../../../../src/ComicManagement.Api",
    "ComicManagement.Api.csproj",
    "0")]

namespace ComicManagement.R03.E2EHost;

internal static class Program
{
    internal const string CertificatePassword = "r03-e2e-certificate-password";

    public static async Task Main(string[] args)
    {
        var port = ParsePort(args);
        var securityDirectory = Path.Combine(
            Path.GetTempPath(),
            $"comic-management-r03-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(securityDirectory);
        CreateDataProtectionCertificate(
            Path.Combine(securityDirectory, "data-protection.pfx"),
            CertificatePassword);

        await using var postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_r03_e2e")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        try
        {
            await postgres.StartAsync();
            await using var factory = new R03E2EApplicationFactory(
                postgres.GetConnectionString(),
                securityDirectory);
            factory.UseKestrel(port);
            factory.StartServer();

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Database.MigrateAsync();
                await SeedAsync(dbContext);
            }

            factory.State.MarkReady();
            Console.WriteLine(FormattableString.Invariant(
                $"R03 real E2E host ready at http://127.0.0.1:{port}."));

            var stopped = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopped.TrySetResult();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stopped.TrySetResult();
            await stopped.Task;
        }
        finally
        {
            if (Directory.Exists(securityDirectory))
            {
                Directory.Delete(securityDirectory, recursive: true);
            }
        }
    }

    private static async Task SeedAsync(AppDbContext dbContext)
    {
        var createdAt = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        var user = User.CreateFromGoogle(
            R03E2EData.UserId,
            "R03 E2E Reader",
            "r03-reader@example.test",
            null,
            createdAt);
        var admin = User.CreateFromGoogle(
            R03E2EData.AdminId,
            "A01 E2E Admin",
            "a01-admin@example.test",
            null,
            createdAt);
        var comic = Comic.Create(
            R03E2EData.ComicId,
            R03E2EData.ComicSlug,
            R03E2EData.ComicTitle,
            "Truyện miễn phí dùng cho R03 integration E2E.",
            ComicSeriesStatuses.Ongoing,
            null,
            0,
            false,
            createdAt);
        var chapter = Chapter.Create(
            R03E2EData.ChapterId,
            comic.Id,
            "Chương 1",
            1,
            0,
            false,
            createdAt);
        var firstPage = CreateReadyPage(
            R03E2EData.FirstPageId,
            chapter.Id,
            1,
            createdAt);
        var secondPage = CreateReadyPage(
            R03E2EData.SecondPageId,
            chapter.Id,
            2,
            createdAt);
        var topUpOrder = PaymentOrder.CreateTopUp(
            R03E2EData.A02PaymentOrderId,
            user.Id,
            "A02-E2E-TOPUP",
            100_000,
            10_000,
            "a02-e2e-order",
            new string('a', 64),
            createdAt,
            createdAt.AddMinutes(30));
        topUpOrder.FulfillTopUp(createdAt.AddMinutes(1));
        var paidCoinLot = PaidCoinLot.Create(
            R03E2EData.A02PaidCoinLotId,
            user.Id,
            topUpOrder.Id,
            10_000,
            createdAt.AddMinutes(1));
        var refund = RefundRequest.Create(
            R03E2EData.A02RefundId,
            topUpOrder.Id,
            user.Id,
            "A02 real integration refund",
            100_000,
            10_000,
            "a02-e2e-refund",
            new string('b', 64),
            "v1",
            createdAt.AddMinutes(2));
        var recipient = RefundRecipient.Create(
            R03E2EData.A02RefundRecipientId,
            refund.Id,
            "VCB",
            [1, 2, 3],
            [4, 5, 6],
            "6789",
            "******6789",
            "A*** R***",
            "v1",
            createdAt.AddMinutes(2));

        chapter.Publish(true, chapter.Version, createdAt.AddMinutes(1));
        comic.Publish(true, comic.Version, createdAt.AddMinutes(2));

        dbContext.Set<User>().AddRange(user, admin);
        dbContext.Set<Comic>().Add(comic);
        dbContext.Set<Chapter>().Add(chapter);
        dbContext.Set<ChapterPage>().AddRange(firstPage, secondPage);
        dbContext.Set<PaymentOrder>().Add(topUpOrder);
        dbContext.Set<PaidCoinLot>().Add(paidCoinLot);
        dbContext.Set<RefundRequest>().Add(refund);
        dbContext.Set<RefundRecipient>().Add(recipient);
        dbContext.Set<AuditLog>().Add(AuditLog.Create(
            R03E2EData.A04AuditId,
            admin.Id,
            "Dashboard.SeedVerified",
            "PaymentOrder",
            topUpOrder.Id,
            "a04-e2e-seed",
            createdAt.AddMinutes(5),
            "{\"status\":\"Succeeded\",\"safeMarker\":\"Q03 browser-safe metadata\"," +
            "\"accountNumber\":\"0123456789\",\"ciphertext\":\"protected-cipher\"," +
            "\"token\":\"token-value\",\"nested\":{" +
            "\"secret\":\"nested-secret-value\",\"safe\":true}}"));
        await dbContext.SaveChangesAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identity.users SET role = 'Admin' WHERE id = {R03E2EData.AdminId}");
    }

    private static ChapterPage CreateReadyPage(
        Guid id,
        Guid chapterId,
        int pageOrder,
        DateTimeOffset createdAt)
    {
        var page = ChapterPage.CreatePending(
            id,
            chapterId,
            pageOrder,
            $"r03-e2e/{chapterId:N}/{pageOrder}",
            $"sha256:{pageOrder:x64}",
            createdAt);
        page.MarkReady(800, 1_200);
        return page;
    }

    private static int ParsePort(string[] args)
    {
        var portIndex = Array.IndexOf(args, "--port");
        if (portIndex < 0 || portIndex + 1 >= args.Length ||
            !int.TryParse(args[portIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65_535)
        {
            throw new ArgumentException("Supply --port with a valid TCP port.", nameof(args));
        }

        return port;
    }

    private static void CreateDataProtectionCertificate(string path, string password)
    {
        using var rsa = RSA.Create(2_048);
        var request = new CertificateRequest(
            "CN=ComicManagement R03 E2E",
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

internal sealed class R03E2EApplicationFactory(
    string connectionString,
    string securityDirectory) : WebApplicationFactory<global::Program>
{
    private readonly string _keysPath = Path.Combine(securityDirectory, "keys");
    private readonly string _certificatePath = Path.Combine(
        securityDirectory,
        "data-protection.pfx");

    internal R03E2EState State { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Database", connectionString)
            .UseSetting("Authentication:Google:ClientId", "r03-e2e-google-client")
            .UseSetting("Authentication:Google:ClientSecret", "r03-e2e-google-secret")
            .UseSetting("Authentication:SessionLifetime", "01:00:00")
            .UseSetting("Cloudinary:CloudName", "r03-e2e-cloud")
            .UseSetting("Cloudinary:ApiKey", "r03-e2e-key")
            .UseSetting("Cloudinary:ApiSecret", "r03-e2e-secret")
            .UseSetting("Cloudinary:MaximumChapterPageBytes", "10485760")
            .UseSetting("Authentication:DataProtectionKeysPath", _keysPath)
            .UseSetting("Authentication:DataProtectionCertificatePath", _certificatePath)
            .UseSetting("Authentication:DataProtectionCertificatePassword", Program.CertificatePassword)
            .UseSetting("Api:ErrorDocumentationBaseUri", "https://docs.example.test/errors")
            .UseSetting("Authentication:LoginRateLimit:PermitLimit", "1000")
            .UseSetting("Authentication:LoginRateLimit:Window", "00:01:00")
            .UseSetting("Billing:RefundSecurity:CurrentFingerprintVersion", "v1")
            .UseSetting(
                "Billing:RefundSecurity:FingerprintKeys:v1",
                "cmYwMS10ZXN0LWZpbmdlcnByaW50LWtleS12MS0zMiE=")
            .UseSetting("Billing:RefundSecurity:CurrentProtectionVersion", "v1")
            .UseSetting("Reading:ProgressRateLimit:PermitLimit", "60")
            .UseSetting("ReverseProxy:KnownNetworks:0", "0.0.0.0/0")
            .UseSetting("ReverseProxy:KnownNetworks:1", "::/0");

        builder.ConfigureServices(services =>
        {
            services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                var hostPart = new AssemblyPart(typeof(R03E2EReadyController).Assembly);
                if (!manager.ApplicationParts.Any(part => part.Name == hostPart.Name))
                {
                    manager.ApplicationParts.Add(hostPart);
                }
            });

            services.RemoveAll<IHostedService>();
            services.RemoveAll<IChapterPageContentProvider>();
            services.RemoveAll<IChapterPageAssetStore>();
            services.AddSingleton<IChapterPageContentProvider, R03E2EChapterPageContentProvider>();
            services.AddSingleton<IChapterPageAssetStore, A01E2EChapterPageAssetStore>();
            services.AddSingleton(State);
            services.PostConfigure<AntiforgeryOptions>(options =>
            {
                options.Cookie.Name = "ComicManagement.R03.E2E.Antiforgery";
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
            });
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = R03E2EAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = R03E2EAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, R03E2EAuthenticationHandler>(
                    R03E2EAuthenticationHandler.SchemeName,
                    _ => { });
        });
    }
}

internal static class R03E2EData
{
    internal static readonly Guid UserId = Guid.Parse("a713606c-d008-4f76-a6e7-d83829303001");
    internal static readonly Guid AdminId = Guid.Parse("a713606c-d008-4f76-a6e7-d8382930a001");
    internal static readonly Guid A02PaymentOrderId = Guid.Parse("a0200000-0000-4000-8000-000000000001");
    internal static readonly Guid A02PaidCoinLotId = Guid.Parse("a0200000-0000-4000-8000-000000000002");
    internal static readonly Guid A02RefundId = Guid.Parse("a0200000-0000-4000-8000-000000000003");
    internal static readonly Guid A02RefundRecipientId = Guid.Parse("a0200000-0000-4000-8000-000000000004");
    internal static readonly Guid A04AuditId = Guid.Parse("a0400000-0000-4000-8000-000000000001");
    internal static readonly Guid ComicId = Guid.Parse("4ee1fdae-79bb-4839-a271-68cac1630301");
    internal static readonly Guid ChapterId = Guid.Parse("2744b2b6-f009-4122-b574-1dad21740301");
    internal static readonly Guid FirstPageId = Guid.Parse("8d62666a-23d2-4f56-818d-7af1843f0301");
    internal static readonly Guid SecondPageId = Guid.Parse("8d62666a-23d2-4f56-818d-7af1843f0302");
    internal const string ComicSlug = "hanh-trinh-r03-that";
    internal const string ComicTitle = "Hành trình R03 thật";
}

public sealed class R03E2EState
{
    private volatile bool _ready;

    internal bool IsReady => _ready;

    internal void MarkReady() => _ready = true;
}

[ApiController]
public sealed class R03E2EReadyController(R03E2EState state) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/__r03-e2e/ready")]
    public IActionResult Get() => state.IsReady
        ? Ok(new { ready = true })
        : StatusCode(StatusCodes.Status503ServiceUnavailable);
}

internal sealed class R03E2EAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "R03.E2E";
    private const string HeaderName = "X-R03-E2E-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) ||
            !Guid.TryParse(values.SingleOrDefault(), out var userId) ||
            userId != R03E2EData.UserId && userId != R03E2EData.AdminId)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, userId == R03E2EData.AdminId ? "A01 E2E Admin" : "R03 E2E Reader"),
            new Claim(ClaimTypes.Email, userId == R03E2EData.AdminId ? "a01-admin@example.test" : "r03-reader@example.test"),
            new Claim(ClaimTypes.Role, userId == R03E2EData.AdminId ? IdentityRoles.Admin : IdentityRoles.User),
            new Claim("comic_management:status", UserStatuses.Active),
            new Claim("comic_management:version", "1"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            SchemeName,
            ClaimTypes.Name,
            ClaimTypes.Role));
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class A01E2EChapterPageAssetStore : IChapterPageAssetStore
{
    public void ValidateUpload(ChapterPageAssetUpload upload)
    {
        if (upload.ContentLength > 10_485_760)
            throw new ArgumentException("The test upload is too large.", nameof(upload));
    }

    public string GetPublicId(Guid chapterId, Guid pageId) =>
        $"a01-e2e/{chapterId:N}/{pageId:N}";

    public Task<ChapterPageAsset> UploadAsync(
        ChapterPageAssetUpload upload,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChapterPageAsset(GetPublicId(upload.ChapterId, upload.PageId), 800, 1_200));

    public Task DeleteAsync(string publicId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class R03E2EChapterPageContentProvider : IChapterPageContentProvider
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2J7sAAAAASUVORK5CYII=");

    public Task<ChapterPageContent?> GetAsync(
        string assetKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ChapterPageContent?>(new ChapterPageContent(Png, "image/png"));
}
