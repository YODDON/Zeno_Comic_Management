using ComicManagement.Identity.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence;

internal sealed class WalletConfiguration : IEntityTypeConfiguration<WalletAccount>
{
    public void Configure(EntityTypeBuilder<WalletAccount> builder)
    {
        builder.ToTable("wallets", DatabaseSchemas.Wallet, table =>
        {
            table.HasCheckConstraint("ck_wallets_paid_available", "paid_available >= 0");
            table.HasCheckConstraint("ck_wallets_paid_reserved", "paid_reserved >= 0");
            table.HasCheckConstraint("ck_wallets_bonus_available", "bonus_available >= 0");
            table.HasCheckConstraint(
                "ck_wallets_total_available",
                "paid_available::numeric + bonus_available::numeric <= 9223372036854775807");
            table.HasCheckConstraint("ck_wallets_version", "version >= 1");
            table.HasCheckConstraint("ck_wallets_timestamps", "updated_at >= created_at");
        });

        builder.HasKey(wallet => wallet.UserId).HasName("pk_wallets");
        builder.Property(wallet => wallet.UserId).HasColumnName("user_id");
        builder.Property(wallet => wallet.PaidAvailable)
            .HasColumnName("paid_available")
            .HasDefaultValue(0L);
        builder.Property(wallet => wallet.PaidReserved)
            .HasColumnName("paid_reserved")
            .HasDefaultValue(0L);
        builder.Property(wallet => wallet.BonusAvailable)
            .HasColumnName("bonus_available")
            .HasDefaultValue(0L);
        builder.Property(wallet => wallet.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        builder.Property(wallet => wallet.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(wallet => wallet.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone");
        builder.Ignore(wallet => wallet.TotalAvailable);

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<WalletAccount>(wallet => wallet.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_wallets_users");
    }
}
