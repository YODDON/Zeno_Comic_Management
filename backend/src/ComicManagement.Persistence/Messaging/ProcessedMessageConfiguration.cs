using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages", DatabaseSchemas.Infrastructure);

        builder.HasKey(message => new { message.Consumer, message.MessageId })
            .HasName("pk_processed_messages");

        builder.Property(message => message.Consumer)
            .HasColumnName("consumer")
            .HasMaxLength(200);
        builder.Property(message => message.MessageId)
            .HasColumnName("message_id");
        builder.Property(message => message.ProcessedAt)
            .HasColumnName("processed_at")
            .HasColumnType("timestamp with time zone");
    }
}
