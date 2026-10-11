using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", DatabaseSchemas.Infrastructure, table =>
        {
            table.HasCheckConstraint(
                "ck_outbox_messages_attempt_count",
                "attempt_count >= 0");
            table.HasCheckConstraint(
                "ck_outbox_messages_status",
                "status IN ('Pending', 'Processing', 'Completed', 'Failed')");
            table.HasCheckConstraint(
                "ck_outbox_messages_lock_state",
                "(status = 'Processing' AND locked_until IS NOT NULL) OR " +
                "(status <> 'Processing' AND locked_until IS NULL)");
        });

        builder.HasKey(message => message.Id)
            .HasName("pk_outbox_messages");

        builder.Property(message => message.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(message => message.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(message => message.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(message => message.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(message => message.AvailableAt)
            .HasColumnName("available_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(message => message.Status)
            .HasColumnName("status")
            .HasMaxLength(24)
            .IsRequired()
            .IsConcurrencyToken();
        builder.Property(message => message.AttemptCount)
            .HasColumnName("attempt_count");
        builder.Property(message => message.LockedUntil)
            .HasColumnName("locked_until")
            .HasColumnType("timestamp with time zone")
            .IsConcurrencyToken();
        builder.Property(message => message.LastErrorRedacted)
            .HasColumnName("last_error_redacted")
            .HasMaxLength(200);

        builder.HasIndex(message => new { message.Status, message.AvailableAt })
            .HasDatabaseName("ix_outbox_messages_status_available_at");
    }
}
