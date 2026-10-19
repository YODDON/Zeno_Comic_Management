using ComicManagement.Identity.Users;
using ComicManagement.Missions.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ActionReceiptConfiguration : IEntityTypeConfiguration<ActionReceipt>
{
    public void Configure(EntityTypeBuilder<ActionReceipt> builder)
    {
        builder.ToTable("action_receipts", DatabaseSchemas.Missions, table =>
        {
            table.HasCheckConstraint(
                "ck_action_receipts_processing_status",
                "processing_status IN ('Pending','Processed')");
            table.HasCheckConstraint(
                "ck_action_receipts_payload_fingerprint",
                "payload_fingerprint ~ '^[0-9a-f]{64}$'");
        });

        builder.HasKey(receipt => receipt.Id)
            .HasName("pk_action_receipts");
        builder.HasIndex(receipt => new { receipt.SourceEventId, receipt.SourceEventType })
            .IsUnique()
            .HasDatabaseName("ux_action_receipts_source_event");
        builder.HasIndex(receipt => new
            {
                receipt.UserId,
                receipt.OccurredLocalDate,
                receipt.ProcessingStatus,
            })
            .HasDatabaseName("ix_action_receipts_user_local_date_status");
        builder.HasIndex(receipt => new
            {
                receipt.ProcessingStatus,
                receipt.ReceivedAt,
                receipt.Id,
            })
            .HasFilter("processing_status = 'Pending'")
            .HasDatabaseName("ix_action_receipts_pending_worker");

        builder.Property(receipt => receipt.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(receipt => receipt.SourceEventId)
            .HasColumnName("source_event_id");
        builder.Property(receipt => receipt.SourceEventType)
            .HasColumnName("source_event_type")
            .HasMaxLength(ActionReceipt.MaximumSourceEventTypeLength)
            .IsRequired();
        builder.Property(receipt => receipt.UserId)
            .HasColumnName("user_id");
        builder.Property(receipt => receipt.SubjectId)
            .HasColumnName("subject_id");
        builder.Property(receipt => receipt.PayloadFingerprint)
            .HasColumnName("payload_fingerprint")
            .HasMaxLength(ActionReceipt.PayloadFingerprintLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(receipt => receipt.OccurredLocalDate)
            .HasColumnName("occurred_local_date")
            .HasColumnType("date");
        builder.Property(receipt => receipt.ReceivedAt)
            .HasColumnName("received_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(receipt => receipt.ProcessingStatus)
            .HasColumnName("processing_status")
            .HasMaxLength(16)
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(receipt => receipt.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_action_receipts_user");
    }
}
