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
using ComicManagement.Wallet.Reservations;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;
using ComicManagement.Reading.Library;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Membership.Plans;
using ComicManagement.Membership.Subscriptions;
using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Progress;
using ComicManagement.Missions.Receipts;
using ComicManagement.Missions.Rewards;
using ComicManagement.Interaction.Ratings;
using ComicManagement.Catalog.Featured;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

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

    internal DbSet<FeaturedComic> FeaturedComics => Set<FeaturedComic>();

    internal DbSet<FeaturedConfiguration> FeaturedConfigurations => Set<FeaturedConfiguration>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    internal DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    internal DbSet<ReadingSession> ReadingSessions => Set<ReadingSession>();

    internal DbSet<ReadingProgress> ReadingProgress => Set<ReadingProgress>();

    internal DbSet<ChapterUnlockQuote> ChapterUnlockQuotes => Set<ChapterUnlockQuote>();

    internal DbSet<ChapterUnlock> ChapterUnlocks => Set<ChapterUnlock>();

    internal DbSet<WalletAccount> Wallets => Set<WalletAccount>();

    internal DbSet<PaidCoinLot> PaidCoinLots => Set<PaidCoinLot>();

    internal DbSet<WalletOperation> WalletOperations => Set<WalletOperation>();

    internal DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();

    internal DbSet<WalletAllocation> WalletAllocations => Set<WalletAllocation>();

    internal DbSet<CoinReservation> CoinReservations => Set<CoinReservation>();

    internal DbSet<ComicFollow> ComicFollows => Set<ComicFollow>();

    internal DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();

    internal DbSet<ProviderTransaction> ProviderTransactions => Set<ProviderTransaction>();

    internal DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    internal DbSet<RefundRequest> RefundRequests => Set<RefundRequest>();

    internal DbSet<RefundRecipient> RefundRecipients => Set<RefundRecipient>();

    internal DbSet<RefundTransferRecord> RefundTransferRecords => Set<RefundTransferRecord>();

    internal DbSet<VipPlan> VipPlans => Set<VipPlan>();

    internal DbSet<VipSubscription> VipSubscriptions => Set<VipSubscription>();

    internal DbSet<VipTransaction> VipTransactions => Set<VipTransaction>();

    internal DbSet<MissionDefinition> MissionDefinitions => Set<MissionDefinition>();

    internal DbSet<MissionRevision> MissionRevisions => Set<MissionRevision>();

    internal DbSet<ActionReceipt> ActionReceipts => Set<ActionReceipt>();

    internal DbSet<MissionActivation> MissionActivations => Set<MissionActivation>();

    internal DbSet<MissionProgress> MissionProgress => Set<MissionProgress>();

    internal DbSet<ProgressAction> ProgressActions => Set<ProgressAction>();

    internal DbSet<MissionRewardGrant> MissionRewardGrants => Set<MissionRewardGrant>();

    internal DbSet<MissionDailyCounter> MissionDailyCounters => Set<MissionDailyCounter>();

    internal DbSet<ComicRating> ComicRatings => Set<ComicRating>();

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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Replace<ForeignKeyIndexConvention>(services =>
            new RefundAwareForeignKeyIndexConvention(
                services.GetRequiredService<ProviderConventionSetBuilderDependencies>()));
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

        if (ChangeTracker.Entries<CoinReservation>().Any(entry =>
                entry.State == EntityState.Deleted ||
                entry.State == EntityState.Modified &&
                (entry.Property(item => item.Id).IsModified ||
                 entry.Property(item => item.RefundRequestId).IsModified ||
                 entry.Property(item => item.UserId).IsModified ||
                 entry.Property(item => item.PaidCoinLotId).IsModified ||
                 entry.Property(item => item.WalletOperationId).IsModified ||
                 entry.Property(item => item.ApprovedBy).IsModified ||
                 entry.Property(item => item.IdempotencyKey).IsModified ||
                 entry.Property(item => item.RequestFingerprint).IsModified ||
                 entry.Property(item => item.AmountCoins).IsModified ||
                 entry.Property(item => item.ApprovalResultStatus).IsModified ||
                 entry.Property(item => item.ApprovalResultVersion).IsModified ||
                 entry.Property(item => item.ApprovalResultReviewedBy).IsModified ||
                 entry.Property(item => item.ApprovalResultReviewedAt).IsModified ||
                 entry.Property(item => item.ApprovalResultDecisionNote).IsModified ||
                 entry.Property(item => item.CreatedAt).IsModified)))
        {
            throw new InvalidOperationException("Coin reservation snapshots are immutable.");
        }

        if (ChangeTracker.Entries<ChapterUnlock>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Chapter unlocks are append-only.");
        }

        if (ChangeTracker.Entries<VipTransaction>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("VIP transactions are append-only.");
        }

        if (ChangeTracker.Entries<ChapterUnlockQuote>().Any(entry =>
                entry.State == EntityState.Modified))
        {
            throw new InvalidOperationException("Chapter unlock quotes are immutable.");
        }

        if (ChangeTracker.Entries<MissionRevision>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Mission revisions are append-only.");
        }

        if (ChangeTracker.Entries<MissionActivation>().Any(entry =>
                entry.State == EntityState.Deleted ||
                entry.State == EntityState.Modified &&
                (entry.Property(item => item.Id).IsModified ||
                 entry.Property(item => item.MissionDefinitionId).IsModified ||
                 entry.Property(item => item.MissionRevisionId).IsModified ||
                 entry.Property(item => item.ActivationSequence).IsModified ||
                 entry.Property(item => item.ActivatedAt).IsModified ||
                 entry.Property(item => item.DeactivatedAt).OriginalValue is not null ||
                 entry.Property(item => item.DeactivatedAt).CurrentValue is null)))
        {
            throw new InvalidOperationException(
                "Mission activations are immutable after their interval is closed.");
        }

        if (ChangeTracker.Entries<RefundRecipient>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Refund recipients are immutable.");
        }

        if (ChangeTracker.Entries<RefundTransferRecord>().Any(entry =>
                entry.State == EntityState.Deleted ||
                entry.State == EntityState.Modified &&
                (entry.Property(item => item.Id).IsModified ||
                 entry.Property(item => item.RefundRequestId).IsModified ||
                 entry.Property(item => item.UserId).IsModified ||
                 entry.Property(item => item.BankReference).IsModified ||
                 entry.Property(item => item.AmountVnd).IsModified ||
                 entry.Property(item => item.EvidenceReference).IsModified ||
                 entry.Property(item => item.RecordedBy).IsModified ||
                 entry.Property(item => item.RecordedAt).IsModified ||
                 entry.Property(item => item.IdempotencyKey).IsModified ||
                 entry.Property(item => item.RequestFingerprint).IsModified ||
                 entry.Property(item => item.VerifiedAt).OriginalValue is not null)))
        {
            throw new InvalidOperationException(
                "Refund transfer records are immutable after verification.");
        }

        if (ChangeTracker.Entries<RefundRequest>().Any(entry =>
                entry.State == EntityState.Deleted ||
                entry.State == EntityState.Modified && RefundSnapshotIsModified(entry)))
        {
            throw new InvalidOperationException("Refund request snapshots are immutable.");
        }

        if (ChangeTracker.Entries<ProgressAction>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<MissionRewardGrant>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Mission actions and reward grants are append-only.");
        }
    }

    private static bool RefundSnapshotIsModified(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<RefundRequest> entry) =>
        entry.Property(request => request.Id).IsModified ||
        entry.Property(request => request.PaymentOrderId).IsModified ||
        entry.Property(request => request.UserId).IsModified ||
        entry.Property(request => request.PaymentOrderType).IsModified ||
        entry.Property(request => request.Reason).IsModified ||
        entry.Property(request => request.OriginalAmountVnd).IsModified ||
        entry.Property(request => request.FeeAmountVnd).IsModified ||
        entry.Property(request => request.NetRefundVnd).IsModified ||
        entry.Property(request => request.RequiredRecoveryCoins).IsModified ||
        entry.Property(request => request.IdempotencyKey).IsModified ||
        entry.Property(request => request.RequestFingerprint).IsModified ||
        entry.Property(request => request.RequestFingerprintVersion).IsModified ||
        entry.Property(request => request.RequestedAt).IsModified;

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
