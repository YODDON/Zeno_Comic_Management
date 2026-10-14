using ComicManagement.Administration.Auditing;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using ComicManagement.Identity.Users;
using ComicManagement.Reading.Sessions;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;
using ComicManagement.Reading.Library;
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

    internal DbSet<WalletAccount> Wallets => Set<WalletAccount>();

    internal DbSet<PaidCoinLot> PaidCoinLots => Set<PaidCoinLot>();

    internal DbSet<WalletOperation> WalletOperations => Set<WalletOperation>();

    internal DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();
    internal DbSet<ComicFollow> ComicFollows => Set<ComicFollow>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAppendOnlyRecords();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAppendOnlyRecords();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Wallet ledger records are append-only.");
        }
    }
}
