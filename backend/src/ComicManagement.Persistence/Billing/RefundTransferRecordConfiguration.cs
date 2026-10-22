using ComicManagement.Billing.Refunds;
using ComicManagement.Identity.Users;
using ComicManagement.Wallet.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class RefundTransferRecordConfiguration :
    IEntityTypeConfiguration<RefundTransferRecord>
{
    public void Configure(EntityTypeBuilder<RefundTransferRecord> builder)
    {
        builder.ToTable("refund_transfer_records", DatabaseSchemas.Billing, table =>
        {
            table.HasCheckConstraint("ck_refund_transfer_records_amount", "amount_vnd > 0");
            table.HasCheckConstraint("ck_refund_transfer_records_status",
                "status IN ('ReportedSucceeded','ReportedFailed','NeedsReconciliation','VerifiedSucceeded','VerifiedFailed')");
            table.HasCheckConstraint("ck_refund_transfer_records_verification",
                "(status IN ('ReportedSucceeded','ReportedFailed','NeedsReconciliation') AND verified_by IS NULL AND verified_at IS NULL AND confirmation_note IS NULL AND verification_idempotency_key IS NULL AND verification_fingerprint IS NULL AND resolution_operation_id IS NULL) OR " +
                "(status IN ('VerifiedSucceeded','VerifiedFailed') AND verified_by IS NOT NULL AND verified_at IS NOT NULL AND confirmation_note IS NOT NULL AND verification_idempotency_key IS NOT NULL AND verification_fingerprint IS NOT NULL AND resolution_operation_id IS NOT NULL)");
        });

        builder.HasKey(item => item.Id).HasName("pk_refund_transfer_records");
        builder.HasIndex(item => item.RefundRequestId)
            .IsUnique().HasDatabaseName("ux_refund_transfer_records_refund_request_id");
        builder.HasIndex(item => new { item.RecordedBy, item.IdempotencyKey })
            .IsUnique().HasDatabaseName("ux_refund_transfer_records_record_idempotency");
        builder.HasIndex(item => item.BankReference)
            .IsUnique().HasDatabaseName("ux_refund_transfer_records_bank_reference");
        builder.HasIndex(item => new { item.VerifiedBy, item.VerificationIdempotencyKey })
            .IsUnique().HasFilter("verification_idempotency_key IS NOT NULL")
            .HasDatabaseName("ux_refund_transfer_records_verify_idempotency");
        builder.HasIndex(item => item.ResolutionOperationId)
            .IsUnique().HasFilter("resolution_operation_id IS NOT NULL")
            .HasDatabaseName("ux_refund_transfer_records_resolution_operation");

        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.RefundRequestId).HasColumnName("refund_request_id");
        builder.Property(item => item.UserId).HasColumnName("user_id");
        builder.Property(item => item.BankReference).HasColumnName("bank_reference")
            .HasMaxLength(RefundTransferRecord.MaximumReferenceLength).IsRequired();
        builder.Property(item => item.AmountVnd).HasColumnName("amount_vnd");
        builder.Property(item => item.EvidenceReference).HasColumnName("evidence_reference")
            .HasMaxLength(RefundTransferRecord.MaximumEvidenceReferenceLength).IsRequired();
        builder.Property(item => item.Status).HasColumnName("status")
            .HasMaxLength(RefundTransferRecord.MaximumStatusLength).IsRequired();
        builder.Property(item => item.RecordedBy).HasColumnName("recorded_by");
        builder.Property(item => item.RecordedAt).HasColumnName("recorded_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key")
            .HasMaxLength(RefundTransferRecord.MaximumIdempotencyKeyLength).IsRequired();
        builder.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint")
            .HasMaxLength(RefundTransferRecord.FingerprintLength).IsRequired();
        builder.Property(item => item.VerifiedBy).HasColumnName("verified_by");
        builder.Property(item => item.VerifiedAt).HasColumnName("verified_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.ConfirmationNote).HasColumnName("confirmation_note")
            .HasMaxLength(RefundTransferRecord.MaximumConfirmationNoteLength);
        builder.Property(item => item.VerificationIdempotencyKey)
            .HasColumnName("verification_idempotency_key")
            .HasMaxLength(RefundTransferRecord.MaximumIdempotencyKeyLength);
        builder.Property(item => item.VerificationFingerprint)
            .HasColumnName("verification_fingerprint")
            .HasMaxLength(RefundTransferRecord.FingerprintLength);
        builder.Property(item => item.ResolutionOperationId)
            .HasColumnName("resolution_operation_id");

        builder.HasOne<RefundRequest>().WithOne()
            .HasForeignKey<RefundTransferRecord>(item => new { item.RefundRequestId, item.UserId })
            .HasPrincipalKey<RefundRequest>(request => new { request.Id, request.UserId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_transfer_records_refund_owner");
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.RecordedBy)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_transfer_records_recorded_by");
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.VerifiedBy)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_transfer_records_verified_by");
        builder.HasOne<WalletOperation>().WithOne()
            .HasForeignKey<RefundTransferRecord>(item => new { item.ResolutionOperationId, item.UserId })
            .HasPrincipalKey<WalletOperation>(operation => new { operation.Id, operation.UserId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_transfer_records_resolution_operation_owner");
    }
}
