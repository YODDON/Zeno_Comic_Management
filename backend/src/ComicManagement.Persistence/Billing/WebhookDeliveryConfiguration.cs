using ComicManagement.Billing.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class WebhookDeliveryConfiguration :
    IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries", DatabaseSchemas.Billing, table =>
        {
            table.HasCheckConstraint(
                "ck_webhook_deliveries_authenticated_transaction",
                "auth_verified = TRUE AND provider_transaction_id IS NOT NULL");
            table.HasCheckConstraint(
                "ck_webhook_deliveries_payload_reference",
                "payload_reference ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_webhook_deliveries_processing",
                "(processing_result IN ('Accepted', 'Duplicate') AND error_code IS NULL) OR " +
                "(processing_result = 'ReconciliationRequired' AND error_code IS NOT NULL)");
        });

        builder.HasKey(delivery => delivery.Id)
            .HasName("pk_webhook_deliveries");
        builder.HasIndex(delivery => delivery.ProviderTransactionId)
            .HasDatabaseName("ix_webhook_deliveries_provider_transaction_id");
        builder.HasIndex(delivery => new { delivery.ReceivedAt, delivery.Id })
            .IsDescending(true, true)
            .HasDatabaseName("ix_webhook_deliveries_received_id");

        builder.Property(delivery => delivery.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(delivery => delivery.ProviderTransactionId)
            .HasColumnName("provider_transaction_id");
        builder.Property(delivery => delivery.ReceivedAt)
            .HasColumnName("received_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(delivery => delivery.AuthVerified)
            .HasColumnName("auth_verified");
        builder.Property(delivery => delivery.PayloadReference)
            .HasColumnName("payload_reference")
            .HasMaxLength(WebhookDelivery.PayloadReferenceLength)
            .IsRequired();
        builder.Property(delivery => delivery.DeliveryReference)
            .HasColumnName("delivery_reference")
            .HasMaxLength(WebhookDelivery.MaximumDeliveryReferenceLength);
        builder.Property(delivery => delivery.ProcessingResult)
            .HasColumnName("processing_result")
            .HasMaxLength(WebhookDelivery.MaximumProcessingResultLength)
            .IsRequired();
        builder.Property(delivery => delivery.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(WebhookDelivery.MaximumErrorCodeLength);

        builder.HasOne<ProviderTransaction>()
            .WithMany()
            .HasForeignKey(delivery => delivery.ProviderTransactionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_webhook_deliveries_provider_transactions");
    }
}
