using ComicManagement.Catalog.Browsing;
using ComicManagement.Catalog.Media;
using ComicManagement.Catalog.Publishing;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Profiles;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Sessions;
using ComicManagement.Reading.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;
using ComicManagement.Wallet.Queries;
using ComicManagement.Billing.Payments;

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
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<IGoogleIdentityStore, GoogleIdentityStore>();
        services.TryAddScoped<IUserProfileStore, UserProfileStore>();
        services.TryAddScoped<ISessionStore, SessionStore>();
        services.TryAddScoped<IChapterPageUploadStore, ChapterPageUploadStore>();
        services.TryAddScoped<ICatalogPublicationStore, CatalogPublicationStore>();
        services.TryAddScoped<CatalogPublicationHandler>();
        services.TryAddScoped<IPublicCatalogStore, PublicCatalogStore>();
        services.TryAddScoped<PublicCatalogHandler>();
        services.TryAddScoped<IChapterAccessProjectionStore, ChapterAccessProjectionStore>();
        services.TryAddScoped<ChapterAccessHandler>();
        services.TryAddScoped<IReadingSessionStore, ReadingSessionStore>();
        services.TryAddScoped<ReadingSessionHandler>();
        services.TryAddScoped<IWalletQueryStore, WalletQueryStore>();
        services.TryAddScoped<WalletQueryHandler>();
        services.TryAddScoped<ITopUpOrderStore, TopUpOrderStore>();
        services.TryAddSingleton<ITopUpOrderCriticalSectionObserver,
            NoOpTopUpOrderCriticalSectionObserver>();
        services.TryAddScoped<TopUpOrderHandler>();
        services.TryAddScoped<ISePayWebhookStore, SePayWebhookStore>();
        services.TryAddSingleton<ISePayWebhookCriticalSectionObserver,
            NoOpSePayWebhookCriticalSectionObserver>();
        services.TryAddScoped<SePayWebhookHandler>();
        services.TryAddScoped<WalletSpendEngine>();
        services.TryAddScoped<ILibraryStore, LibraryStore>();
        services.TryAddScoped<LibraryHandler>();
        services.TryAddScoped<IOutboxWriter, EfOutboxWriter>();
        services.TryAddScoped<OutboxMessageDispatcher>();
        services.TryAddScoped<OutboxBatchProcessor>();
        services.AddOptions<OutboxOptions>();

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
