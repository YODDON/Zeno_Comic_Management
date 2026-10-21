using ComicManagement.Billing.Refunds;
using ComicManagement.Identity.Users;
using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class CoinReservationConfiguration : IEntityTypeConfiguration<CoinReservation>
{
    public void Configure(EntityTypeBuilder<CoinReservation> builder)
    {
        builder.ToTable("coin_reservations", DatabaseSchemas.Wallet, table =>
        {
            table.HasCheckConstraint("ck_coin_reservations_amount", "amount_coins > 0");
            table.HasCheckConstraint(
                "ck_coin_reservations_status",
                "status IN ('Reserved', 'Consumed', 'Released')");
            table.HasCheckConstraint(
                "ck_coin_reservations_resolution",
                "(status = 'Reserved' AND resolved_at IS NULL) OR " +
                "(status IN ('Consumed', 'Released') AND resolved_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_coin_reservations_idempotency",
                "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) " +
                "AND request_fingerprint ~ '^[0-9a-f]{64}$'");
        });

        builder.HasKey(reservation => reservation.Id).HasName("pk_coin_reservations");
        builder.HasIndex(reservation => reservation.RefundRequestId)
            .IsUnique()
            .HasDatabaseName("ux_coin_reservations_refund_request_id");
        builder.HasIndex(reservation => reservation.WalletOperationId)
            .IsUnique()
            .HasDatabaseName("ux_coin_reservations_wallet_operation_id");
        builder.HasIndex(reservation => new
            {
                reservation.ApprovedBy,
                reservation.IdempotencyKey,
            })
            .IsUnique()
            .HasDatabaseName("ux_coin_reservations_approver_idempotency");
        builder.HasIndex(reservation => new
            {
                reservation.PaidCoinLotId,
                reservation.Status,
            })
            .HasDatabaseName("ix_coin_reservations_lot_status");

        builder.Property(reservation => reservation.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(reservation => reservation.RefundRequestId)
            .HasColumnName("refund_request_id");
        builder.Property(reservation => reservation.UserId).HasColumnName("user_id");
        builder.Property(reservation => reservation.PaidCoinLotId)
            .HasColumnName("paid_coin_lot_id");
        builder.Property(reservation => reservation.WalletOperationId)
            .HasColumnName("wallet_operation_id");
        builder.Property(reservation => reservation.ApprovedBy).HasColumnName("approved_by");
        builder.Property(reservation => reservation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(CoinReservation.MaximumIdempotencyKeyLength)
            .IsRequired();
        builder.Property(reservation => reservation.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(CoinReservation.FingerprintLength)
            .IsRequired();
        builder.Property(reservation => reservation.AmountCoins).HasColumnName("amount_coins");
        builder.Property(reservation => reservation.Status)
            .HasColumnName("status")
            .HasMaxLength(CoinReservation.MaximumStatusLength)
            .IsRequired();
        builder.Property(reservation => reservation.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(reservation => reservation.ResolvedAt)
            .HasColumnName("resolved_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<RefundRequest>()
            .WithOne()
            .HasForeignKey<CoinReservation>(reservation => new
            {
                reservation.RefundRequestId,
                reservation.UserId,
            })
            .HasPrincipalKey<RefundRequest>(request => new { request.Id, request.UserId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_coin_reservations_refund_request_owner");
        builder.HasOne<PaidCoinLot>()
            .WithMany()
            .HasForeignKey(reservation => new
            {
                reservation.PaidCoinLotId,
                reservation.UserId,
            })
            .HasPrincipalKey(lot => new { lot.Id, lot.UserId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_coin_reservations_paid_lot_owner");
        builder.HasOne<WalletOperation>()
            .WithOne()
            .HasForeignKey<CoinReservation>(reservation => new
            {
                reservation.WalletOperationId,
                reservation.UserId,
            })
            .HasPrincipalKey<WalletOperation>(operation => new
            {
                operation.Id,
                operation.UserId,
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_coin_reservations_wallet_operation_owner");
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(reservation => reservation.ApprovedBy)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_coin_reservations_approved_by_user");
    }
}
