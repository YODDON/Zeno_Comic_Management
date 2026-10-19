using ComicManagement.Identity.Users;
using ComicManagement.Missions.Progress;
using ComicManagement.Missions.Rewards;
using ComicManagement.Wallet.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class MissionRewardGrantConfiguration : IEntityTypeConfiguration<MissionRewardGrant>
{
    public void Configure(EntityTypeBuilder<MissionRewardGrant> builder)
    {
        builder.ToTable("reward_grants", DatabaseSchemas.Missions, table =>
        {
            table.HasCheckConstraint("ck_reward_grants_bonus", "bonus_coins BETWEEN 1 AND 300");
            table.HasCheckConstraint(
                "ck_reward_grants_outcome",
                "outcome IN ('Granted','DeniedDailyCap')");
            table.HasCheckConstraint(
                "ck_reward_grants_wallet_operation",
                "(outcome = 'Granted' AND wallet_operation_id IS NOT NULL) OR " +
                "(outcome = 'DeniedDailyCap' AND wallet_operation_id IS NULL)");
        });
        builder.HasKey(item => item.Id).HasName("pk_reward_grants");
        builder.HasIndex(item => item.ProgressId).IsUnique()
            .HasDatabaseName("ux_reward_grants_progress");
        builder.HasIndex(item => item.WalletOperationId).IsUnique()
            .HasFilter("wallet_operation_id IS NOT NULL")
            .HasDatabaseName("ux_reward_grants_wallet_operation");
        builder.HasIndex(item => new { item.UserId, item.DecidedAt, item.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_reward_grants_user_decided_id");
        builder.HasIndex(item => new { item.ProgressId, item.UserId })
            .HasDatabaseName("ix_reward_grants_progress_owner");
        builder.HasIndex(item => new { item.WalletOperationId, item.UserId })
            .HasDatabaseName("ix_reward_grants_wallet_operation_owner");
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.ProgressId).HasColumnName("progress_id");
        builder.Property(item => item.UserId).HasColumnName("user_id");
        builder.Property(item => item.BonusCoins).HasColumnName("bonus_coins");
        builder.Property(item => item.Outcome).HasColumnName("outcome").HasMaxLength(24).IsRequired();
        builder.Property(item => item.LocalDate).HasColumnName("local_date").HasColumnType("date");
        builder.Property(item => item.DecidedAt).HasColumnName("decided_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.WalletOperationId).HasColumnName("wallet_operation_id");
        builder.HasOne<MissionProgress>().WithMany()
            .HasForeignKey(item => new { item.ProgressId, item.UserId })
            .HasPrincipalKey(progress => new { progress.Id, progress.UserId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reward_grants_progress_owner");
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_reward_grants_user");
        builder.HasOne<WalletOperation>().WithMany()
            .HasForeignKey(item => new { Id = item.WalletOperationId, item.UserId })
            .HasPrincipalKey(operation => new { operation.Id, operation.UserId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reward_grants_wallet_operation_owner");
    }
}

internal sealed class MissionDailyCounterConfiguration : IEntityTypeConfiguration<MissionDailyCounter>
{
    public void Configure(EntityTypeBuilder<MissionDailyCounter> builder)
    {
        builder.ToTable("daily_counters", DatabaseSchemas.Missions, table =>
        {
            table.HasCheckConstraint(
                "ck_daily_counters_granted_total",
                "granted_total BETWEEN 0 AND 300");
            table.HasCheckConstraint("ck_daily_counters_version", "version >= 1");
        });
        builder.HasKey(item => new { item.UserId, item.LocalDate })
            .HasName("pk_daily_counters");
        builder.Property(item => item.UserId).HasColumnName("user_id");
        builder.Property(item => item.LocalDate).HasColumnName("local_date").HasColumnType("date");
        builder.Property(item => item.GrantedTotal).HasColumnName("granted_total");
        builder.Property(item => item.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_daily_counters_user");
    }
}
