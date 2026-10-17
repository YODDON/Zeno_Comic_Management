using ComicManagement.Administration.Auditing;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using ComicManagement.Identity.Users;
using ComicManagement.Reading.Sessions;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Spending;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;
using ComicManagement.Reading.Library;
using ComicManagement.Billing.Payments;
using ComicManagement.Membership.Plans;
using ComicManagement.Membership.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    internal DbSet<User> Users => Set<User>();

    internal DbSet<GoogleIdentity> GoogleIdentities => Set<GoogleIdentity>();

    internal DbSet<Comic> Comics => Set<Comic>();

    internal DbSet<Chapter> Chapters => Set<Chapter>();

    internal DbSet<ChapterPage> ChapterPages => Set<ChapterPage>();

    internal DbSet<Genre> Genres => Set<Genre>();

    internal DbSet<Author> Authors => Set<Author>();

    internal DbSet<TranslationGroup> TranslationGroups => Set<TranslationGroup>();

    internal DbSet<ComicGenre> ComicGenres => Set<ComicGenre>();

    internal DbSet<ComicAuthor> ComicAuthors => Set<ComicAuthor>();

    internal DbSet<ComicTranslationGroup> ComicTranslationGroups => Set<ComicTranslationGroup>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    internal DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    internal DbSet<ReadingSession> ReadingSessions => Set<ReadingSession>();

    internal DbSet<ReadingProgress> ReadingProgress => Set<ReadingProgress>();

    internal DbSet<ChapterUnlockQuote> ChapterUnlockQuotes => Set<ChapterUnlockQuote>();

    internal DbSet<WalletAccount> Wallets => Set<WalletAccount>();

    internal DbSet<PaidCoinLot> PaidCoinLots => Set<PaidCoinLot>();

    internal DbSet<WalletOperation> WalletOperations => Set<WalletOperation>();

    internal DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();

    internal DbSet<WalletAllocation> WalletAllocations => Set<WalletAllocation>();

    internal DbSet<ComicFollow> ComicFollows => Set<ComicFollow>();

    internal DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();

    internal DbSet<ProviderTransaction> ProviderTransactions => Set<ProviderTransaction>();

    internal DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    internal DbSet<VipPlan> VipPlans => Set<VipPlan>();

    internal DbSet<VipSubscription> VipSubscriptions => Set<VipSubscription>();

    internal DbSet<VipTransaction> VipTransactions => Set<VipTransaction>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAppendOnlyRecords();
        EnsurePaidCoinLotsReferenceTopUpOrders();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAppendOnlyRecords();
        await EnsurePaidCoinLotsReferenceTopUpOrdersAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    private void EnsureAppendOnlyRecords()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit logs are append-only.");
        }

        if (ChangeTracker.Entries<WalletOperation>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<WalletEntry>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<WalletAllocation>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Wallet ledger records are append-only.");
        }

        if (ChangeTracker.Entries<VipTransaction>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("VIP transactions are append-only.");
        }

        if (ChangeTracker.Entries<ChapterUnlockQuote>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Chapter unlock quotes are append-only.");
        }
    }

    private void EnsurePaidCoinLotsReferenceTopUpOrders()
    {
        var orderIds = AddedPaidCoinLotOrderIds();
        if (orderIds.Count == 0)
        {
            return;
        }

        var invalidTrackedOrder = ChangeTracker.Entries<PaymentOrder>()
            .Any(entry => orderIds.Contains(entry.Entity.Id) &&
                          entry.Entity.OrderType != PaymentOrderTypes.TopUp);
        var trackedOrderIds = ChangeTracker.Entries<PaymentOrder>()
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var databaseOrderIds = orderIds.Except(trackedOrderIds).ToArray();
        var invalidPersistedOrder = databaseOrderIds.Length > 0 &&
            PaymentOrders.AsNoTracking().Any(order =>
                databaseOrderIds.Contains(order.Id) &&
                order.OrderType != PaymentOrderTypes.TopUp);
        if (invalidTrackedOrder || invalidPersistedOrder)
        {
            throw new InvalidOperationException(
                "PaidCoinLot must reference a TopUp PaymentOrder.");
        }
    }

    private async Task EnsurePaidCoinLotsReferenceTopUpOrdersAsync(
        CancellationToken cancellationToken)
    {
        var orderIds = AddedPaidCoinLotOrderIds();
        if (orderIds.Count == 0)
        {
            return;
        }

        var invalidTrackedOrder = ChangeTracker.Entries<PaymentOrder>()
            .Any(entry => orderIds.Contains(entry.Entity.Id) &&
                          entry.Entity.OrderType != PaymentOrderTypes.TopUp);
        var trackedOrderIds = ChangeTracker.Entries<PaymentOrder>()
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var databaseOrderIds = orderIds.Except(trackedOrderIds).ToArray();
        var invalidPersistedOrder = databaseOrderIds.Length > 0 &&
            await PaymentOrders.AsNoTracking().AnyAsync(
                order => databaseOrderIds.Contains(order.Id) &&
                         order.OrderType != PaymentOrderTypes.TopUp,
                cancellationToken);
        if (invalidTrackedOrder || invalidPersistedOrder)
        {
            throw new InvalidOperationException(
                "PaidCoinLot must reference a TopUp PaymentOrder.");
        }
    }

    private HashSet<Guid> AddedPaidCoinLotOrderIds() => ChangeTracker
        .Entries<PaidCoinLot>()
        .Where(entry => entry.State == EntityState.Added)
        .Select(entry => entry.Entity.TopUpOrderId)
        .ToHashSet();
}
