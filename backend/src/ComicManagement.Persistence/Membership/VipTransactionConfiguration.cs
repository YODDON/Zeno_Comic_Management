using ComicManagement.Billing.Payments;
using ComicManagement.Identity.Users;
using ComicManagement.Membership.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class VipTransactionConfiguration :
    IEntityTypeConfiguration<VipTransaction>
{
    public void Configure(EntityTypeBuilder<VipTransaction> builder)
    {
        builder.ToTable("vip_transactions", DatabaseSchemas.Membership, table =>
        {
            table.HasCheckConstraint(
                "ck_vip_transactions_order_type",
                "payment_order_type = 'VipPurchase'");
            table.HasCheckConstraint(
                "ck_vip_transactions_duration",
                "duration_hours = 720");
            table.HasCheckConstraint(
                "ck_vip_transactions_subscription_version",
                "subscription_version >= 1 AND " +
                "((subscription_version = 1 AND previous_expiry IS NULL) OR " +
                "(subscription_version > 1 AND previous_expiry IS NOT NULL))");
            table.HasCheckConstraint(
                "ck_vip_transactions_timeline",
                "effective_start >= fulfilled_at AND " +
                "new_expiry = effective_start + make_interval(hours => duration_hours) AND " +
                "created_at = fulfilled_at AND " +
                "(previous_expiry IS NULL OR effective_start >= previous_expiry)");
        });

        builder.HasKey(transaction => transaction.Id).HasName("pk_vip_transactions");
        builder.HasIndex(transaction => transaction.PaymentOrderId)
            .IsUnique()
            .HasDatabaseName("ux_vip_transactions_payment_order_id");
        builder.HasIndex(transaction => new
        {
            transaction.UserId,
            transaction.SubscriptionVersion,
        })
            .IsUnique()
            .IsDescending(false, true)
            .HasDatabaseName("ux_vip_transactions_user_subscription_version");

        builder.Property(transaction => transaction.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(transaction => transaction.PaymentOrderId)
            .HasColumnName("payment_order_id");
        builder.Property(transaction => transaction.UserId).HasColumnName("user_id");
        builder.Property(transaction => transaction.SubscriptionVersion)
            .HasColumnName("subscription_version");
        builder.Property(transaction => transaction.PaymentOrderType)
            .HasColumnName("payment_order_type")
            .HasMaxLength(VipTransaction.MaximumPaymentOrderTypeLength)
            .IsRequired();
        builder.Property(transaction => transaction.PreviousExpiry)
            .HasColumnName("previous_expiry")
            .HasColumnType("timestamp with time zone");
        builder.Property(transaction => transaction.EffectiveStart)
            .HasColumnName("effective_start")
            .HasColumnType("timestamp with time zone");
        builder.Property(transaction => transaction.NewExpiry)
            .HasColumnName("new_expiry")
            .HasColumnType("timestamp with time zone");
        builder.Property(transaction => transaction.DurationHours)
            .HasColumnName("duration_hours");
        builder.Property(transaction => transaction.FulfilledAt)
            .HasColumnName("fulfilled_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(transaction => transaction.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<PaymentOrder>()
            .WithMany()
            .HasForeignKey(transaction => new
            {
                transaction.PaymentOrderId,
                transaction.UserId,
                transaction.PaymentOrderType,
            })
            .HasPrincipalKey(order => new { order.Id, order.UserId, order.OrderType })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_vip_transactions_payment_order_owner_type");
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(transaction => transaction.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_vip_transactions_users");
    }
}
