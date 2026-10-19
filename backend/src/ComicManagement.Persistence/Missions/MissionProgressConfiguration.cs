using ComicManagement.Identity.Users;
using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class MissionProgressConfiguration : IEntityTypeConfiguration<MissionProgress>
{
    public void Configure(EntityTypeBuilder<MissionProgress> builder)
    {
        builder.ToTable("mission_progress", DatabaseSchemas.Missions, table =>
        {
            table.HasCheckConstraint("ck_mission_progress_count", "progress_count >= 0");
            table.HasCheckConstraint("ck_mission_progress_version", "version >= 1");
            table.HasCheckConstraint(
                "ck_mission_progress_status",
                "status IN ('InProgress','Completed')");
            table.HasCheckConstraint(
                "ck_mission_progress_completion",
                "(status = 'InProgress' AND completed_at IS NULL) OR " +
                "(status = 'Completed' AND completed_at IS NOT NULL)");
        });

        builder.HasKey(item => item.Id).HasName("pk_mission_progress");
        builder.HasAlternateKey(item => new { item.Id, item.UserId })
            .HasName("ak_mission_progress_id_user_id");
        builder.HasIndex(item => new { item.UserId, item.MissionRevisionId, item.PeriodKey })
            .IsUnique()
            .HasDatabaseName("ux_mission_progress_user_revision_period");
        builder.HasIndex(item => new { item.UserId, item.Status })
            .HasDatabaseName("ix_mission_progress_user_status");
        builder.HasIndex(item => item.MissionRevisionId)
            .HasDatabaseName("ix_mission_progress_revision");

        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.UserId).HasColumnName("user_id");
        builder.Property(item => item.MissionRevisionId).HasColumnName("mission_revision_id");
        builder.Property(item => item.PeriodKey)
            .HasColumnName("period_key")
            .HasMaxLength(MissionProgress.MaximumPeriodKeyLength)
            .IsRequired();
        builder.Property(item => item.ProgressCount).HasColumnName("progress_count");
        builder.Property(item => item.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        builder.Property(item => item.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.Version).HasColumnName("version").IsConcurrencyToken();

        builder.HasOne<User>().WithMany().HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_mission_progress_user");
        builder.HasOne<MissionRevision>().WithMany().HasForeignKey(item => item.MissionRevisionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_mission_progress_revision");
    }
}

internal sealed class ProgressActionConfiguration : IEntityTypeConfiguration<ProgressAction>
{
    public void Configure(EntityTypeBuilder<ProgressAction> builder)
    {
        builder.ToTable("progress_actions", DatabaseSchemas.Missions);
        builder.HasKey(item => new { item.MissionProgressId, item.DedupKey })
            .HasName("pk_progress_actions");
        builder.HasIndex(item => item.SourceEventId)
            .HasDatabaseName("ix_progress_actions_source_event");
        builder.Property(item => item.MissionProgressId).HasColumnName("mission_progress_id");
        builder.Property(item => item.SourceEventId).HasColumnName("source_event_id");
        builder.Property(item => item.SubjectId).HasColumnName("subject_id");
        builder.Property(item => item.DedupKey).HasColumnName("dedup_key")
            .HasMaxLength(ProgressAction.MaximumDedupKeyLength).IsRequired();
        builder.Property(item => item.OccurredAt).HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone");
        builder.HasOne<MissionProgress>().WithMany().HasForeignKey(item => item.MissionProgressId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_progress_actions_progress");
    }
}
