using ComicManagement.Catalog.Browsing;
using ComicManagement.Catalog.Media;
using ComicManagement.Catalog.Publishing;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Profiles;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Sessions;
using ComicManagement.Reading.Library;
using ComicManagement.Reading.Unlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;
using ComicManagement.Wallet.Queries;
using ComicManagement.Membership.Memberships;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Missions.Periods;
using ComicManagement.Missions.Administration;
using ComicManagement.Missions.Processing;
using ComicManagement.Missions.Queries;
using ComicManagement.Interaction.Ratings;
using ComicManagement.Interaction.Comments;
using ComicManagement.Shared.Persistence;
using ComicManagement.Catalog.Administration;
using ComicManagement.Catalog.Featured;
using ComicManagement.Administration.Finance;
using ComicManagement.Identity.Administration;
using ComicManagement.Administration.Reporting;

namespace ComicManagement.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<AppDbContext>(options =>
            ConfigurePostgreSql(options, connectionString));
        services.AddLogging();
        services.TryAddScoped<IConsistentReadExecutor, PostgreSqlConsistentReadExecutor>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<IGoogleIdentityStore, GoogleIdentityStore>();
        services.TryAddScoped<IUserProfileStore, UserProfileStore>();
        services.TryAddScoped<IUserAdministrationStore, UserAdministrationStore>();
        services.TryAddScoped<UserAdministrationHandler>();
        services.TryAddScoped<ISessionStore, SessionStore>();
        services.TryAddScoped<IChapterPageUploadStore, ChapterPageUploadStore>();
        services.TryAddScoped<UploadChapterPageHandler>();
        services.TryAddScoped<ICatalogPublicationStore, CatalogPublicationStore>();
        services.TryAddScoped<CatalogPublicationHandler>();
        services.TryAddScoped<ICatalogAdministrationStore, CatalogAdministrationStore>();
        services.TryAddSingleton<ICatalogAdministrationCriticalSectionObserver, NoOpCatalogAdministrationCriticalSectionObserver>();
        services.TryAddScoped<CatalogAdministrationHandler>();
        services.TryAddScoped<IAdminChapterPageService, AdminChapterPageService>();
        services.TryAddSingleton<IAdminChapterPageCriticalSectionObserver,
            NoOpAdminChapterPageCriticalSectionObserver>();
        services.TryAddScoped<IFeaturedCatalogStore, FeaturedCatalogStore>();
        services.TryAddSingleton<IFeaturedCatalogCriticalSectionObserver, NoOpFeaturedCatalogCriticalSectionObserver>();
        services.TryAddScoped<IPublicCatalogStore, PublicCatalogStore>();
        services.TryAddScoped<PublicCatalogHandler>();
        services.TryAddScoped<IComicRatingStore, ComicRatingStore>();
        services.TryAddSingleton<IComicRatingReadObserver, NoOpComicRatingReadObserver>();
        services.TryAddScoped<ComicRatingHandler>();
        services.TryAddScoped<ICommentStore, CommentStore>();
        services.TryAddScoped<CommentHandler>();
        services.TryAddScoped<IChapterAccessProjectionStore, ChapterAccessProjectionStore>();
        services.TryAddScoped<ChapterAccessHandler>();
        services.TryAddScoped<IChapterUnlockQuoteStore, ChapterUnlockQuoteStore>();
        services.TryAddScoped<ChapterUnlockQuoteHandler>();
        services.TryAddScoped<IChapterUnlockStore, ChapterUnlockStore>();
        services.TryAddSingleton<IChapterUnlockCriticalSectionObserver,
            NoOpChapterUnlockCriticalSectionObserver>();
        services.TryAddScoped<ChapterUnlockHandler>();
        services.TryAddScoped<IReadingSessionStore, ReadingSessionStore>();
        services.TryAddScoped<ReadingSessionHandler>();
        services.TryAddScoped<IWalletQueryStore, WalletQueryStore>();
        services.TryAddScoped<WalletQueryHandler>();
        services.TryAddScoped<ITopUpOrderStore, TopUpOrderStore>();
        services.TryAddSingleton<ITopUpOrderCriticalSectionObserver,
            NoOpTopUpOrderCriticalSectionObserver>();
        services.TryAddScoped<TopUpOrderHandler>();
        services.TryAddScoped<IRefundRequestStore, RefundRequestStore>();
        services.TryAddScoped<IRefundFinancialProcessingLoader, RefundFinancialProcessingLoader>();
        services.TryAddScoped<IRefundAdministrationStore, RefundAdministrationStore>();
        services.TryAddScoped<IFinanceAdministrationQueryStore, FinanceAdministrationQueryStore>();
        services.TryAddScoped<FinanceAdministrationQueryHandler>();
        services.TryAddScoped<IAdministrationReportingStore, AdministrationReportingStore>();
        services.TryAddSingleton<IAdministrationReportingReadObserver,
            NoOpAdministrationReportingReadObserver>();
        services.TryAddScoped<AdministrationReportingHandler>();
        services.TryAddSingleton<IRefundRequestCriticalSectionObserver,
            NoOpRefundRequestCriticalSectionObserver>();
        services.TryAddSingleton<IRefundApprovalCriticalSectionObserver,
            NoOpRefundApprovalCriticalSectionObserver>();
        services.TryAddSingleton<IRefundPayoutCriticalSectionObserver,
            NoOpRefundPayoutCriticalSectionObserver>();
        services.TryAddScoped<RefundRequestHandler>();
        services.TryAddScoped<ISePayWebhookStore, SePayWebhookStore>();
        services.TryAddSingleton<ISePayWebhookCriticalSectionObserver,
            NoOpSePayWebhookCriticalSectionObserver>();
        services.TryAddSingleton<ISePayFulfillmentCriticalSectionObserver,
            NoOpSePayFulfillmentCriticalSectionObserver>();
        services.TryAddScoped<WalletSpendEngine>();
        services.TryAddScoped<ILibraryStore, LibraryStore>();
        services.TryAddScoped<LibraryHandler>();
        services.TryAddScoped<IVipMembershipStore, VipMembershipStore>();
        services.TryAddSingleton<IVipCheckoutCriticalSectionObserver,
            NoOpVipCheckoutCriticalSectionObserver>();
        services.TryAddScoped<VipMembershipHandler>();
        services.TryAddSingleton<MissionPeriodResolver>();
        services.TryAddScoped<IMissionAdministrationStore, MissionAdministrationStore>();
        services.TryAddSingleton<IMissionActivationCriticalSectionObserver,
            NoOpMissionActivationCriticalSectionObserver>();
        services.TryAddScoped<MissionAdministrationHandler>();
        services.TryAddScoped<IMissionRewardAdministrationStore, MissionRewardAdministrationStore>();
        services.TryAddScoped<MissionRewardAdministrationHandler>();
        services.TryAddScoped<MissionActionReceiptRecorder>();
        services.TryAddScoped<IMissionReceiptProcessingStore, MissionReceiptProcessingStore>();
        services.TryAddSingleton<IMissionReceiptCriticalSectionObserver,
            NoOpMissionReceiptCriticalSectionObserver>();
        services.TryAddScoped<MissionReceiptProcessor>();
        services.TryAddScoped<IMissionQueryStore, MissionQueryStore>();
        services.TryAddScoped<MissionQueryHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxMessageHandler,
            UserLoggedInMissionReceiptHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxMessageHandler,
            ChapterReadQualifiedMissionReceiptHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxMessageHandler,
            ComicFollowedMissionReceiptHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxMessageHandler,
            ChapterUnlockedMissionReceiptHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxMessageHandler,
            WalletTopUpCompletedMissionReceiptHandler>());
        services.TryAddScoped<IOutboxWriter, EfOutboxWriter>();
        services.TryAddScoped<OutboxMessageDispatcher>();
        services.TryAddScoped<OutboxBatchProcessor>();
        services.AddOptions<OutboxOptions>();

        return services;
    }

    public static IServiceCollection AddMissionReceiptWorker(
        this IServiceCollection services,
        Action<MissionReceiptOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<MissionReceiptOptions>()
            .Validate(value => value.BatchSize is >= 1 and <= 100,
                "Missions receipt BatchSize must be between 1 and 100.")
            .Validate(value => value.PollInterval > TimeSpan.Zero,
                "Missions receipt PollInterval must be positive.")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddHostedService<MissionReceiptWorker>();
        return services;
    }

    public static IServiceCollection AddOutboxWorker(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services
            .AddOptions<OutboxOptions>()
            .Validate(value => value.BatchSize is >= 1 and <= 100, "BatchSize must be between 1 and 100.")
            .Validate(value => value.PollInterval > TimeSpan.Zero, "PollInterval must be positive.")
            .Validate(value => value.LockDuration > TimeSpan.Zero, "LockDuration must be positive.")
            .Validate(value => value.BaseRetryDelay > TimeSpan.Zero, "BaseRetryDelay must be positive.")
            .Validate(
                value => value.MaxRetryDelay >= value.BaseRetryDelay,
                "MaxRetryDelay must be greater than or equal to BaseRetryDelay.")
            .Validate(value => value.MaxAttempts >= 1, "MaxAttempts must be positive.")
            .ValidateOnStart();

        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddHostedService<OutboxWorker>();
        return services;
    }

    internal static void ConfigurePostgreSql(
        DbContextOptionsBuilder options,
        string connectionString)
    {
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
            npgsqlOptions.MigrationsHistoryTable(
                "__ef_migrations_history",
                DatabaseSchemas.Infrastructure);
        });
    }
}
