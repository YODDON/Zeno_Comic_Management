using ComicManagement.Administration.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs", DatabaseSchemas.Administration, table =>
        {
            table.HasCheckConstraint("ck_audit_logs_action_not_blank", "length(btrim(action)) > 0");
            table.HasCheckConstraint(
                "ck_audit_logs_target_type_not_blank",
                "length(btrim(target_type)) > 0");
            table.HasCheckConstraint(
                "ck_audit_logs_correlation_id_not_blank",
                "length(btrim(correlation_id)) > 0");
        });

        builder.HasKey(audit => audit.Id).HasName("pk_audit_logs");
        builder.HasIndex(audit => new { audit.TargetType, audit.TargetId, audit.OccurredAt })
            .HasDatabaseName("ix_audit_logs_target_occurred_at");
        builder.HasIndex(audit => new { audit.ActorId, audit.OccurredAt })
            .HasDatabaseName("ix_audit_logs_actor_occurred_at");
        builder.HasIndex(audit => new { audit.OccurredAt, audit.Id })
            .IsDescending()
            .HasDatabaseName("ix_audit_logs_occurred_at_id");

        builder.Property(audit => audit.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(audit => audit.ActorId).HasColumnName("actor_id");
        builder.Property(audit => audit.Action)
            .HasColumnName("action")
            .HasMaxLength(AuditLog.MaximumActionLength)
            .IsRequired();
        builder.Property(audit => audit.TargetType)
            .HasColumnName("target_type")
            .HasMaxLength(AuditLog.MaximumTargetTypeLength)
            .IsRequired();
        builder.Property(audit => audit.TargetId).HasColumnName("target_id");
        builder.Property(audit => audit.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(AuditLog.MaximumCorrelationIdLength)
            .IsRequired();
        builder.Property(audit => audit.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(audit => audit.RedactedChanges)
            .HasColumnName("redacted_changes")
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
