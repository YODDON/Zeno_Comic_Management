using ComicManagement.Catalog.Chapters;
using ComicManagement.Identity.Users;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Wallet.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ChapterUnlockConfiguration : IEntityTypeConfiguration<ChapterUnlock>
{
    public void Configure(EntityTypeBuilder<ChapterUnlock> builder)
    {
        builder.ToTable("chapter_unlocks", DatabaseSchemas.Reading, table =>
        {
            table.HasCheckConstraint(
                "ck_chapter_unlocks_spend",
                "price_paid_coins > 0 AND bonus_spent_coins >= 0 AND paid_spent_coins >= 0 " +
                "AND price_paid_coins::numeric = bonus_spent_coins::numeric + paid_spent_coins::numeric");
            table.HasCheckConstraint(
                "ck_chapter_unlocks_wallet_after",
                "wallet_paid_available_after >= 0 AND wallet_bonus_available_after >= 0");
        });

        builder.HasKey(unlock => unlock.Id);
        builder.Property(unlock => unlock.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(unlock => unlock.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(unlock => unlock.ChapterId).HasColumnName("chapter_id").IsRequired();
        builder.Property(unlock => unlock.QuoteId).HasColumnName("quote_id").IsRequired();
        builder.Property(unlock => unlock.WalletOperationId)
            .HasColumnName("wallet_operation_id")
            .IsRequired();
        builder.Property(unlock => unlock.PricePaidCoins)
            .HasColumnName("price_paid_coins")
            .IsRequired();
        builder.Property(unlock => unlock.BonusSpentCoins)
            .HasColumnName("bonus_spent_coins")
            .IsRequired();
        builder.Property(unlock => unlock.PaidSpentCoins)
            .HasColumnName("paid_spent_coins")
            .IsRequired();
        builder.Property(unlock => unlock.WalletPaidAvailableAfter)
            .HasColumnName("wallet_paid_available_after")
            .IsRequired();
        builder.Property(unlock => unlock.WalletBonusAvailableAfter)
            .HasColumnName("wallet_bonus_available_after")
            .IsRequired();
        builder.Property(unlock => unlock.UnlockedAt)
            .HasColumnName("unlocked_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(unlock => new { unlock.UserId, unlock.ChapterId })
            .IsUnique()
            .HasDatabaseName("ux_chapter_unlocks_user_chapter");
        builder.HasIndex(unlock => unlock.QuoteId)
            .IsUnique()
            .HasDatabaseName("ux_chapter_unlocks_quote");
        builder.HasIndex(unlock => unlock.WalletOperationId)
            .IsUnique()
            .HasDatabaseName("ux_chapter_unlocks_wallet_operation");
        builder.HasIndex(unlock => unlock.ChapterId)
            .HasDatabaseName("ix_chapter_unlocks_chapter");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(unlock => unlock.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Chapter>()
            .WithMany()
            .HasForeignKey(unlock => unlock.ChapterId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ChapterUnlockQuote>()
            .WithMany()
            .HasForeignKey(unlock => new
            {
                unlock.QuoteId,
                unlock.UserId,
                unlock.ChapterId,
            })
            .HasPrincipalKey(quote => new
            {
                quote.Id,
                quote.UserId,
                quote.ChapterId,
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WalletOperation>()
            .WithMany()
            .HasForeignKey(unlock => new { unlock.WalletOperationId, unlock.UserId })
            .HasPrincipalKey(operation => new { operation.Id, operation.UserId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
