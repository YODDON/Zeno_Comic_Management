using ComicManagement.Identity.Users;
using ComicManagement.Billing.Payments;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class PaidCoinLotConfiguration : IEntityTypeConfiguration<PaidCoinLot>
{
    public void Configure(EntityTypeBuilder<PaidCoinLot> builder)
    {
        builder.ToTable("paid_coin_lots", DatabaseSchemas.Wallet, table =>
        {
            table.HasCheckConstraint("ck_paid_coin_lots_granted", "granted_coins > 0");
            table.HasCheckConstraint("ck_paid_coin_lots_spent", "spent_coins >= 0");
            table.HasCheckConstraint("ck_paid_coin_lots_refunded", "refunded_coins >= 0");
            table.HasCheckConstraint("ck_paid_coin_lots_reserved", "reserved_coins >= 0");
            table.HasCheckConstraint(
                "ck_paid_coin_lots_capacity",
                "granted_coins::numeric >= spent_coins::numeric + refunded_coins::numeric + reserved_coins::numeric");
            table.HasCheckConstraint("ck_paid_coin_lots_version", "version >= 1");
            table.HasCheckConstraint(
                "ck_paid_coin_lots_topup_order_type",
                "topup_order_type = 'TopUp'");
        });

        builder.HasKey(lot => lot.Id).HasName("pk_paid_coin_lots");
        builder.HasAlternateKey(lot => new { lot.Id, lot.UserId })
            .HasName("ak_paid_coin_lots_id_user_id");
        builder.HasAlternateKey(lot => new
            {
                lot.TopUpOrderId,
                lot.UserId,
                lot.TopUpOrderType,
                lot.GrantedCoins,
            })
            .HasName("ak_paid_coin_lots_refund_snapshot");
        builder.HasIndex(lot => lot.TopUpOrderId)
            .IsUnique()
            .HasDatabaseName("ux_paid_coin_lots_topup_order_id");
        builder.HasIndex(lot => new { lot.TopUpOrderId, lot.UserId })
            .IsUnique()
            .HasDatabaseName("ux_paid_coin_lots_topup_order_user");
        builder.HasIndex(lot => new
            {
                lot.TopUpOrderId,
                lot.UserId,
                lot.TopUpOrderType,
            })
            .IsUnique()
            .HasDatabaseName("ux_paid_coin_lots_topup_order_owner_type");
        builder.HasIndex(lot => new { lot.UserId, lot.CreatedAt, lot.Id })
            .HasDatabaseName("ix_paid_coin_lots_user_created_id");

        builder.Property(lot => lot.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(lot => lot.UserId).HasColumnName("user_id");
        builder.Property(lot => lot.TopUpOrderId).HasColumnName("topup_order_id");
        builder.Property(lot => lot.TopUpOrderType)
            .HasColumnName("topup_order_type")
            .HasMaxLength(PaymentOrder.MaximumTypeLength)
            .IsRequired();
        builder.Property(lot => lot.GrantedCoins).HasColumnName("granted_coins");
        builder.Property(lot => lot.SpentCoins)
            .HasColumnName("spent_coins")
            .HasDefaultValue(0L);
        builder.Property(lot => lot.RefundedCoins)
            .HasColumnName("refunded_coins")
            .HasDefaultValue(0L);
        builder.Property(lot => lot.ReservedCoins)
            .HasColumnName("reserved_coins")
            .HasDefaultValue(0L);
        builder.Property(lot => lot.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(lot => lot.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        builder.Ignore(lot => lot.AvailableCoins);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(lot => lot.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_paid_coin_lots_users");
        builder.HasOne<PaymentOrder>()
            .WithOne()
            .HasForeignKey<PaidCoinLot>(lot => new
            {
                lot.TopUpOrderId,
                lot.UserId,
                lot.TopUpOrderType,
            })
            .HasPrincipalKey<PaymentOrder>(order => new
            {
                order.Id,
                order.UserId,
                order.OrderType,
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_paid_coin_lots_topup_payment_orders");
    }
}
