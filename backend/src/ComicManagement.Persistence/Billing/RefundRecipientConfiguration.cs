using ComicManagement.Billing.Refunds;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class RefundRecipientConfiguration : IEntityTypeConfiguration<RefundRecipient>
{
    public void Configure(EntityTypeBuilder<RefundRecipient> builder)
    {
        builder.ToTable("refund_recipients", DatabaseSchemas.Billing, table =>
        {
            table.HasCheckConstraint(
                "ck_refund_recipients_ciphertext",
                "octet_length(account_number_encrypted) > 0 " +
                "AND octet_length(account_holder_encrypted) > 0");
            table.HasCheckConstraint(
                "ck_refund_recipients_last4",
                "account_number_last4 ~ '^[0-9]{4}$'");
        });

        builder.HasKey(recipient => recipient.Id).HasName("pk_refund_recipients");
        builder.HasIndex(recipient => recipient.RefundRequestId)
            .IsUnique()
            .HasDatabaseName("ux_refund_recipients_refund_request_id");

        builder.Property(recipient => recipient.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(recipient => recipient.RefundRequestId).HasColumnName("refund_request_id");
        builder.Property(recipient => recipient.BankCode)
            .HasColumnName("bank_code")
            .HasMaxLength(RefundRecipient.MaximumBankCodeLength)
            .IsRequired();
        builder.Property(recipient => recipient.AccountNumberEncrypted)
            .HasColumnName("account_number_encrypted")
            .IsRequired();
        builder.Property(recipient => recipient.AccountHolderEncrypted)
            .HasColumnName("account_holder_encrypted")
            .IsRequired();
        builder.Property(recipient => recipient.AccountNumberLast4)
            .HasColumnName("account_number_last4")
            .HasMaxLength(4)
            .IsRequired();
        builder.Property(recipient => recipient.AccountNumberMasked)
            .HasColumnName("account_number_masked")
            .HasMaxLength(RefundRecipient.MaximumMaskLength)
            .IsRequired();
        builder.Property(recipient => recipient.AccountHolderMasked)
            .HasColumnName("account_holder_masked")
            .HasMaxLength(RefundRecipient.MaximumMaskLength)
            .IsRequired();
        builder.Property(recipient => recipient.EncryptionKeyVersion)
            .HasColumnName("encryption_key_version")
            .HasMaxLength(RefundRecipient.MaximumKeyVersionLength)
            .IsRequired();
        builder.Property(recipient => recipient.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<RefundRequest>()
            .WithOne()
            .HasForeignKey<RefundRecipient>(recipient => recipient.RefundRequestId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_recipients_refund_requests");
    }
}
