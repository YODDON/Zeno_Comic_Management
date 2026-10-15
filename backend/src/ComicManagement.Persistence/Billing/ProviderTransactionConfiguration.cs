using ComicManagement.Billing.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ProviderTransactionConfiguration :
    IEntityTypeConfiguration<ProviderTransaction>
{
    public void Configure(EntityTypeBuilder<ProviderTransaction> builder)
    {
        builder.ToTable("provider_transactions", DatabaseSchemas.Billing, table =>
        {
            table.HasCheckConstraint(
                "ck_provider_transactions_provider",
                "provider IN ('SePay')");
            table.HasCheckConstraint(
                "ck_provider_transactions_amount",
                "amount_vnd > 0");
            table.HasCheckConstraint(
                "ck_provider_transactions_direction",
                "direction IN ('Incoming', 'Outgoing')");
            table.HasCheckConstraint(
                "ck_provider_transactions_payload_fingerprint",
                "payload_fingerprint ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_provider_transactions_processing",
                "(processing_status = 'Received' AND reconciliation_reason IS NULL) OR " +
                "(processing_status = 'ReconciliationRequired' AND reconciliation_reason IS NOT NULL)");
        });

        builder.HasKey(transaction => transaction.Id)
            .HasName("pk_provider_transactions");
        builder.HasIndex(transaction => new
            {
                transaction.Provider,
                transaction.ProviderTransactionId,
            })
            .IsUnique()
            .HasDatabaseName("ux_provider_transactions_provider_external_id");
        builder.HasIndex(transaction => new
            {
                transaction.ProcessingStatus,
                transaction.VerifiedAt,
                transaction.Id,
            })
            .HasDatabaseName("ix_provider_transactions_status_verified_id");
        builder.HasIndex(transaction => transaction.MatchedOrderId)
            .HasDatabaseName("ix_provider_transactions_matched_order_id");

        builder.Property(transaction => transaction.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(transaction => transaction.Provider)
            .HasColumnName("provider")
            .HasMaxLength(ProviderTransaction.MaximumProviderLength)
            .IsRequired();
        builder.Property(transaction => transaction.ProviderTransactionId)
            .HasColumnName("provider_transaction_id")
            .HasMaxLength(ProviderTransaction.MaximumProviderTransactionIdLength)
            .IsRequired();
        builder.Property(transaction => transaction.PayloadFingerprint)
            .HasColumnName("payload_fingerprint")
            .HasMaxLength(ProviderTransaction.PayloadFingerprintLength)
            .IsRequired();
        builder.Property(transaction => transaction.AmountVnd)
            .HasColumnName("amount_vnd");
        builder.Property(transaction => transaction.Direction)
            .HasColumnName("direction")
            .HasMaxLength(ProviderTransaction.MaximumDirectionLength)
            .IsRequired();
        builder.Property(transaction => transaction.PaymentCode)
            .HasColumnName("payment_code")
            .HasMaxLength(ProviderTransaction.MaximumPaymentCodeLength);
        builder.Property(transaction => transaction.AccountNumberLast4)
            .HasColumnName("account_number_last4")
            .HasMaxLength(ProviderTransaction.MaximumAccountNumberLast4Length)
            .IsRequired();
        builder.Property(transaction => transaction.ReferenceCode)
            .HasColumnName("reference_code")
            .HasMaxLength(ProviderTransaction.MaximumReferenceCodeLength);
        builder.Property(transaction => transaction.MatchedOrderId)
            .HasColumnName("matched_order_id");
        builder.Property(transaction => transaction.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(transaction => transaction.VerifiedAt)
            .HasColumnName("verified_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(transaction => transaction.ProcessingStatus)
            .HasColumnName("processing_status")
            .HasMaxLength(ProviderTransaction.MaximumProcessingStatusLength)
            .IsRequired();
        builder.Property(transaction => transaction.ReconciliationReason)
            .HasColumnName("reconciliation_reason")
            .HasMaxLength(ProviderTransaction.MaximumReconciliationReasonLength);

        builder.HasOne<PaymentOrder>()
            .WithMany()
            .HasForeignKey(transaction => transaction.MatchedOrderId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_provider_transactions_payment_orders");
    }
}
