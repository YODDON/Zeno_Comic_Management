using ComicManagement.Billing.Payments;
using ComicManagement.Identity.Users;
using ComicManagement.Membership.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class PaymentOrderConfiguration : IEntityTypeConfiguration<PaymentOrder>
{
    public void Configure(EntityTypeBuilder<PaymentOrder> builder)
    {
        builder.ToTable("payment_orders", DatabaseSchemas.Billing, table =>
        {
            table.HasCheckConstraint(
                "ck_payment_orders_amount",
                "amount_vnd > 0");
            table.HasCheckConstraint(
                "ck_payment_orders_expiry",
                "expires_at > created_at");
            table.HasCheckConstraint(
                "ck_payment_orders_order_status",
                "(order_status = 'Pending' AND expired_at IS NULL " +
                "AND provider_payment_status = 'Pending' AND fulfillment_status = 'NotStarted' " +
                "AND verified_paid_at IS NULL AND fulfilled_at IS NULL) OR " +
                "(order_status = 'Expired' AND expired_at IS NOT NULL AND expired_at >= expires_at " +
                "AND provider_payment_status = 'Pending' AND fulfillment_status = 'NotStarted' " +
                "AND verified_paid_at IS NULL AND fulfilled_at IS NULL) OR " +
                "(order_status = 'Succeeded' AND expired_at IS NULL " +
                "AND provider_payment_status = 'Verified' AND fulfillment_status = 'Fulfilled' " +
                "AND verified_paid_at IS NOT NULL AND fulfilled_at = verified_paid_at " +
                "AND fulfilled_at < expires_at)");
            table.HasCheckConstraint(
                "ck_payment_orders_type_snapshots",
                "(order_type = 'TopUp' AND amount_vnd BETWEEN 10000 AND 2000000 " +
                "AND amount_vnd % 1000 = 0 AND paid_coins_snapshot = amount_vnd / 10 " +
                "AND vip_plan_id IS NULL AND vip_duration_hours_snapshot IS NULL " +
                "AND vip_discount_percent_snapshot IS NULL) OR " +
                "(order_type = 'VipPurchase' AND paid_coins_snapshot IS NULL " +
                "AND vip_plan_id IS NOT NULL AND vip_duration_hours_snapshot = 720 " +
                "AND vip_discount_percent_snapshot = 20)");
            table.HasCheckConstraint(
                "ck_payment_orders_provider",
                "provider IN ('SePay')");
            table.HasCheckConstraint(
                "ck_payment_orders_provider_status",
                "provider_payment_status IN ('Pending', 'Verified', 'Rejected', 'ReconciliationRequired')");
            table.HasCheckConstraint(
                "ck_payment_orders_fulfillment_status",
                "fulfillment_status IN ('NotStarted', 'Retryable', 'Fulfilled', 'ManualReview')");
            table.HasCheckConstraint(
                "ck_payment_orders_idempotency",
                "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) " +
                "AND request_fingerprint ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_payment_orders_version",
                "version >= 1");
        });

        builder.HasKey(order => order.Id).HasName("pk_payment_orders");
        builder.HasAlternateKey(order => new { order.Id, order.UserId })
            .HasName("ak_payment_orders_id_user_id");
        builder.HasAlternateKey(order => new { order.Id, order.UserId, order.OrderType })
            .HasName("ak_payment_orders_id_user_id_order_type");
        builder.HasAlternateKey(order => new
            {
                order.Id,
                order.UserId,
                order.OrderType,
                order.AmountVnd,
            })
            .HasName("ak_payment_orders_refund_snapshot");
        builder.HasIndex(order => order.OrderCode)
            .IsUnique()
            .HasDatabaseName("ux_payment_orders_order_code");
        builder.HasIndex(order => new { order.UserId, order.OrderType, order.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_payment_orders_user_operation_idempotency");
        builder.HasIndex(order => new { order.UserId, order.CreatedAt, order.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_payment_orders_user_created_id");
        builder.HasIndex(order => new { order.ProviderPaymentStatus, order.ExpiresAt })
            .HasDatabaseName("ix_payment_orders_pending_expiry");

        builder.Property(order => order.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(order => order.UserId).HasColumnName("user_id");
        builder.Property(order => order.OrderCode)
            .HasColumnName("order_code")
            .HasMaxLength(PaymentOrder.MaximumOrderCodeLength)
            .IsRequired();
        builder.Property(order => order.OrderType)
            .HasColumnName("order_type")
            .HasMaxLength(PaymentOrder.MaximumTypeLength)
            .IsRequired();
        builder.Property(order => order.AmountVnd).HasColumnName("amount_vnd");
        builder.Property(order => order.PaidCoinsSnapshot).HasColumnName("paid_coins_snapshot");
        builder.Property(order => order.VipPlanId).HasColumnName("vip_plan_id");
        builder.Property(order => order.VipDurationHoursSnapshot)
            .HasColumnName("vip_duration_hours_snapshot");
        builder.Property(order => order.VipDiscountPercentSnapshot)
            .HasColumnName("vip_discount_percent_snapshot");
        builder.Property(order => order.Provider)
            .HasColumnName("provider")
            .HasMaxLength(PaymentOrder.MaximumProviderLength)
            .IsRequired();
        builder.Property(order => order.ProviderPaymentStatus)
            .HasColumnName("provider_payment_status")
            .HasMaxLength(PaymentOrder.MaximumStatusLength)
            .IsRequired();
        builder.Property(order => order.FulfillmentStatus)
            .HasColumnName("fulfillment_status")
            .HasMaxLength(PaymentOrder.MaximumStatusLength)
            .IsRequired();
        builder.Property(order => order.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(PaymentOrder.MaximumIdempotencyKeyLength)
            .IsRequired();
        builder.Property(order => order.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(PaymentOrder.RequestFingerprintLength)
            .IsRequired();
        builder.Property(order => order.OrderStatus)
            .HasColumnName("order_status")
            .HasMaxLength(PaymentOrder.MaximumStatusLength)
            .IsRequired();
        builder.Property(order => order.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(order => order.ExpiredAt)
            .HasColumnName("expired_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(order => order.VerifiedPaidAt)
            .HasColumnName("verified_paid_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(order => order.FulfilledAt)
            .HasColumnName("fulfilled_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(order => order.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(order => order.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(order => order.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_payment_orders_users");
        builder.HasOne<VipPlan>()
            .WithMany()
            .HasForeignKey(order => order.VipPlanId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_payment_orders_vip_plans");
    }
}
