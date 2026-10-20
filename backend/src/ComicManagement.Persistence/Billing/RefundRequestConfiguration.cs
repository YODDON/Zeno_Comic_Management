using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using ComicManagement.Identity.Users;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class RefundRequestConfiguration : IEntityTypeConfiguration<RefundRequest>
{
    public void Configure(EntityTypeBuilder<RefundRequest> builder)
    {
        builder.ToTable("refund_requests", DatabaseSchemas.Billing, table =>
        {
            table.HasCheckConstraint(
                "ck_refund_requests_amounts",
                "original_amount_vnd > 0 AND fee_amount_vnd = original_amount_vnd * 5 / 100 " +
                "AND net_refund_vnd = original_amount_vnd - fee_amount_vnd");
            table.HasCheckConstraint(
                "ck_refund_requests_recovery",
                "required_recovery_coins > 0");
            table.HasCheckConstraint(
                "ck_refund_requests_status",
                "status IN ('Requested', 'UnderReview', 'RecoveryReserved', 'TransferPending', " +
                "'Completed', 'Rejected', 'NeedsReconciliation', 'TransferConfirmedFailed', " +
                "'ReservationReleased')");
            table.HasCheckConstraint(
                "ck_refund_requests_order_type",
                "payment_order_type = 'TopUp'");
            table.HasCheckConstraint(
                "ck_refund_requests_idempotency",
                "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) " +
                "AND request_fingerprint ~ '^[0-9a-f]{64}$' " +
                "AND request_fingerprint_version ~ '^[A-Za-z0-9_-]{1,40}$'");
            table.HasCheckConstraint("ck_refund_requests_version", "version >= 1");
        });

        builder.HasKey(request => request.Id).HasName("pk_refund_requests");
        builder.HasAlternateKey(request => new { request.Id, request.UserId })
            .HasName("ak_refund_requests_id_user_id");
        builder.HasIndex(request => request.PaymentOrderId)
            .IsUnique()
            .HasDatabaseName("ux_refund_requests_payment_order_id");
        builder.HasIndex(request => new { request.UserId, request.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_refund_requests_user_idempotency");
        builder.HasIndex(request => new { request.UserId, request.RequestedAt, request.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_refund_requests_user_requested_id");

        builder.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(request => request.PaymentOrderId).HasColumnName("payment_order_id");
        builder.Property(request => request.UserId).HasColumnName("user_id");
        builder.Property(request => request.PaymentOrderType)
            .HasColumnName("payment_order_type")
            .HasMaxLength(PaymentOrder.MaximumTypeLength)
            .IsRequired();
        builder.Property(request => request.Reason)
            .HasColumnName("reason")
            .HasMaxLength(RefundRequest.MaximumReasonLength)
            .IsRequired();
        builder.Property(request => request.OriginalAmountVnd).HasColumnName("original_amount_vnd");
        builder.Property(request => request.FeeAmountVnd).HasColumnName("fee_amount_vnd");
        builder.Property(request => request.NetRefundVnd).HasColumnName("net_refund_vnd");
        builder.Property(request => request.RequiredRecoveryCoins)
            .HasColumnName("required_recovery_coins");
        builder.Property(request => request.Status)
            .HasColumnName("status")
            .HasMaxLength(RefundRequest.MaximumStatusLength)
            .IsRequired();
        builder.Property(request => request.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(RefundRequest.MaximumIdempotencyKeyLength)
            .IsRequired();
        builder.Property(request => request.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(RefundRequest.FingerprintLength)
            .IsRequired();
        builder.Property(request => request.RequestFingerprintVersion)
            .HasColumnName("request_fingerprint_version")
            .HasMaxLength(RefundRequest.MaximumSecurityVersionLength)
            .IsRequired();
        builder.Property(request => request.RequestedAt)
            .HasColumnName("requested_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(request => request.ReviewedBy).HasColumnName("reviewed_by");
        builder.Property(request => request.ReviewedAt)
            .HasColumnName("reviewed_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(request => request.ApprovedBy).HasColumnName("approved_by");
        builder.Property(request => request.ApprovedAt)
            .HasColumnName("approved_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(request => request.CompletedBy).HasColumnName("completed_by");
        builder.Property(request => request.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(request => request.DecisionReason)
            .HasColumnName("decision_reason")
            .HasMaxLength(RefundRequest.MaximumReasonLength);
        builder.Property(request => request.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasOne<PaymentOrder>()
            .WithOne()
            .HasForeignKey<RefundRequest>(request => new
            {
                request.PaymentOrderId,
                request.UserId,
                request.PaymentOrderType,
                request.OriginalAmountVnd,
            })
            .HasPrincipalKey<PaymentOrder>(order => new
            {
                order.Id,
                order.UserId,
                order.OrderType,
                order.AmountVnd,
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_requests_topup_payment_order_snapshot");
        builder.HasOne<PaidCoinLot>()
            .WithOne()
            .HasForeignKey<RefundRequest>(request => new
            {
                TopUpOrderId = request.PaymentOrderId,
                request.UserId,
                TopUpOrderType = request.PaymentOrderType,
                GrantedCoins = request.RequiredRecoveryCoins,
            })
            .HasPrincipalKey<PaidCoinLot>(lot => new
            {
                lot.TopUpOrderId,
                lot.UserId,
                lot.TopUpOrderType,
                lot.GrantedCoins,
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_requests_original_paid_coin_lot_snapshot");
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(request => request.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_requests_users");

        // payment_order_id is already unique, so it covers the left-prefix lookup used by
        // both snapshot foreign keys. Remove EF's redundant convention indexes.
        RemoveConventionIndex(
            builder,
            nameof(RefundRequest.PaymentOrderId),
            nameof(RefundRequest.UserId),
            nameof(RefundRequest.PaymentOrderType),
            nameof(RefundRequest.OriginalAmountVnd));
        RemoveConventionIndex(
            builder,
            nameof(RefundRequest.PaymentOrderId),
            nameof(RefundRequest.UserId),
            nameof(RefundRequest.PaymentOrderType),
            nameof(RefundRequest.RequiredRecoveryCoins));
    }

    private static void RemoveConventionIndex(
        EntityTypeBuilder<RefundRequest> builder,
        params string[] propertyNames)
    {
        var properties = propertyNames
            .Select(name => builder.Metadata.FindProperty(name)!)
            .ToArray();
        var index = builder.Metadata.FindIndex(properties);
        if (index is not null)
        {
            builder.Metadata.RemoveIndex(index);
        }
    }
}
