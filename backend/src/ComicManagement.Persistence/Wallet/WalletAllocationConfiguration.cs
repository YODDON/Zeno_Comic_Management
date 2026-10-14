using ComicManagement.Wallet.Ledger;
using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Spending;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class WalletAllocationConfiguration : IEntityTypeConfiguration<WalletAllocation>
{
    public void Configure(EntityTypeBuilder<WalletAllocation> builder)
    {
        builder.ToTable("wallet_allocations", DatabaseSchemas.Wallet, table =>
        {
            table.HasCheckConstraint(
                "ck_wallet_allocations_amount",
                "amount_coins > 0");
        });

        builder.HasKey(allocation => allocation.Id).HasName("pk_wallet_allocations");
        builder.HasIndex(allocation => new
        {
            allocation.OperationId,
            allocation.PaidCoinLotId,
        })
            .IsUnique()
            .HasDatabaseName("ux_wallet_allocations_operation_lot");
        builder.HasIndex(allocation => allocation.PaidCoinLotId)
            .HasDatabaseName("ix_wallet_allocations_paid_coin_lot_id");

        builder.Property(allocation => allocation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(allocation => allocation.OperationId)
            .HasColumnName("operation_id");
        builder.Property(allocation => allocation.PaidCoinLotId)
            .HasColumnName("paid_coin_lot_id");
        builder.Property(allocation => allocation.AmountCoins)
            .HasColumnName("amount_coins");
        builder.Property(allocation => allocation.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<WalletOperation>()
            .WithMany()
            .HasForeignKey(allocation => allocation.OperationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_wallet_allocations_operations");
        builder.HasOne<PaidCoinLot>()
            .WithMany()
            .HasForeignKey(allocation => allocation.PaidCoinLotId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_wallet_allocations_paid_coin_lots");
    }
}
