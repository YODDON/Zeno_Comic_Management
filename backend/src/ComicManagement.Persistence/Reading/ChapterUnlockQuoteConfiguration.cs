using ComicManagement.Catalog.Chapters;
using ComicManagement.Identity.Users;
using ComicManagement.Reading.Unlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ChapterUnlockQuoteConfiguration :
    IEntityTypeConfiguration<ChapterUnlockQuote>
{
    public void Configure(EntityTypeBuilder<ChapterUnlockQuote> builder)
    {
        builder.ToTable("chapter_unlock_quotes", DatabaseSchemas.Reading, table =>
        {
            table.HasCheckConstraint(
                "ck_chapter_unlock_quotes_price",
                "base_price_coins > 0 AND final_price_coins > 0 AND " +
                "((vip_discount_applied AND vip_discount_percent = 20 AND " +
                "final_price_coins = CEIL(base_price_coins::numeric * 80 / 100)::bigint) OR " +
                "(NOT vip_discount_applied AND vip_discount_percent = 0 AND " +
                "final_price_coins = base_price_coins))");
            table.HasCheckConstraint(
                "ck_chapter_unlock_quotes_estimate",
                "bonus_to_spend >= 0 AND paid_to_spend >= 0 AND " +
                "bonus_to_spend::numeric + paid_to_spend::numeric <= final_price_coins::numeric");
            table.HasCheckConstraint(
                "ck_chapter_unlock_quotes_expiry",
                "expires_at = created_at + interval '5 minutes'");
            table.HasCheckConstraint(
                "ck_chapter_unlock_quotes_revision",
                "price_revision ~ '^[0-9a-f]{64}$'");
        });

        builder.HasKey(quote => quote.Id).HasName("pk_chapter_unlock_quotes");
        builder.HasAlternateKey(quote => new { quote.Id, quote.UserId, quote.ChapterId })
            .HasName("ak_chapter_unlock_quotes_id_user_chapter");
        builder.HasIndex(quote => new { quote.UserId, quote.ExpiresAt })
            .HasDatabaseName("ix_chapter_unlock_quotes_user_expiry");
        builder.HasIndex(quote => new { quote.ChapterId, quote.ExpiresAt })
            .HasDatabaseName("ix_chapter_unlock_quotes_chapter_expiry");

        builder.Property(quote => quote.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(quote => quote.UserId).HasColumnName("user_id");
        builder.Property(quote => quote.ChapterId).HasColumnName("chapter_id");
        builder.Property(quote => quote.BasePriceCoins).HasColumnName("base_price_coins");
        builder.Property(quote => quote.VipDiscountApplied)
            .HasColumnName("vip_discount_applied");
        builder.Property(quote => quote.VipDiscountPercent)
            .HasColumnName("vip_discount_percent");
        builder.Property(quote => quote.FinalPriceCoins).HasColumnName("final_price_coins");
        builder.Property(quote => quote.BonusToSpend).HasColumnName("bonus_to_spend");
        builder.Property(quote => quote.PaidToSpend).HasColumnName("paid_to_spend");
        builder.Property(quote => quote.PriceRevision)
            .HasColumnName("price_revision")
            .HasMaxLength(ChapterUnlockQuote.PriceRevisionLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(quote => quote.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(quote => quote.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(quote => quote.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_chapter_unlock_quotes_users");
        builder.HasOne<Chapter>()
            .WithMany()
            .HasForeignKey(quote => quote.ChapterId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_chapter_unlock_quotes_chapters");
    }
}
