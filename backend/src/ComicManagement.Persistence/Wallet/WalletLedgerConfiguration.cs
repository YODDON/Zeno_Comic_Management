using ComicManagement.Identity.Users;
using ComicManagement.Wallet.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class WalletOperationConfiguration : IEntityTypeConfiguration<WalletOperation>
{
    public void Configure(EntityTypeBuilder<WalletOperation> builder)
    {
        builder.ToTable("wallet_operations", DatabaseSchemas.Wallet, table =>
        {
            table.HasCheckConstraint("ck_wallet_operations_operation_type", "length(btrim(operation_type)) > 0");
            table.HasCheckConstraint("ck_wallet_operations_source_type", "length(btrim(source_type)) > 0");
            table.HasCheckConstraint(
                "ck_wallet_operations_idempotency_key",
                "idempotency_key IS NULL OR length(btrim(idempotency_key)) > 0");
        });

        builder.HasKey(operation => operation.Id).HasName("pk_wallet_operations");
        builder.HasAlternateKey(operation => new { operation.Id, operation.UserId })
            .HasName("ak_wallet_operations_id_user_id");
        builder.HasIndex(operation => new
        {
            operation.OperationType,
            operation.SourceType,
            operation.SourceId,
        })
            .IsUnique()
            .HasDatabaseName("ux_wallet_operations_business_source");
        builder.HasIndex(operation => new
        {
            operation.UserId,
            operation.OperationType,
            operation.IdempotencyKey,
        })
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL")
            .HasDatabaseName("ux_wallet_operations_user_type_idempotency");

        builder.Property(operation => operation.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(operation => operation.UserId).HasColumnName("user_id");
        builder.Property(operation => operation.OperationType)
            .HasColumnName("operation_type")
            .HasMaxLength(WalletOperation.MaximumOperationTypeLength)
            .IsRequired();
        builder.Property(operation => operation.SourceType)
            .HasColumnName("source_type")
            .HasMaxLength(WalletOperation.MaximumSourceTypeLength)
            .IsRequired();
        builder.Property(operation => operation.SourceId).HasColumnName("source_id");
        builder.Property(operation => operation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(WalletOperation.MaximumIdempotencyKeyLength);
        builder.Property(operation => operation.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(operation => operation.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_wallet_operations_users");
    }
}

internal sealed class WalletEntryConfiguration : IEntityTypeConfiguration<WalletEntry>
{
    public void Configure(EntityTypeBuilder<WalletEntry> builder)
    {
        builder.ToTable("wallet_entries", DatabaseSchemas.Wallet, table =>
        {
            table.HasCheckConstraint("ck_wallet_entries_coin_type", "coin_type IN ('Paid', 'Bonus')");
            table.HasCheckConstraint("ck_wallet_entries_bucket", "bucket IN ('Available', 'Reserved')");
            table.HasCheckConstraint(
                "ck_wallet_entries_bonus_bucket",
                "coin_type <> 'Bonus' OR bucket = 'Available'");
            table.HasCheckConstraint("ck_wallet_entries_delta", "delta_coins <> 0");
            table.HasCheckConstraint("ck_wallet_entries_leg_order", "leg_order >= 1");
        });

        builder.HasKey(entry => entry.Id).HasName("pk_wallet_entries");
        builder.HasIndex(entry => new { entry.OperationId, entry.LegOrder })
            .IsUnique()
            .HasDatabaseName("ux_wallet_entries_operation_leg");
        builder.HasIndex(entry => new { entry.UserId, entry.CreatedAt, entry.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_wallet_entries_user_created_id");

        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entry => entry.OperationId).HasColumnName("operation_id");
        builder.Property(entry => entry.UserId).HasColumnName("user_id");
        builder.Property(entry => entry.CoinType).HasColumnName("coin_type").HasMaxLength(16).IsRequired();
        builder.Property(entry => entry.Bucket).HasColumnName("bucket").HasMaxLength(16).IsRequired();
        builder.Property(entry => entry.DeltaCoins).HasColumnName("delta_coins");
        builder.Property(entry => entry.LegOrder).HasColumnName("leg_order");
        builder.Property(entry => entry.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<WalletOperation>()
            .WithMany()
            .HasForeignKey(entry => new { entry.OperationId, entry.UserId })
            .HasPrincipalKey(operation => new { operation.Id, operation.UserId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_wallet_entries_operation_owner");
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_wallet_entries_users");
    }
}
