using ComicManagement.Missions.Definitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class MissionRevisionConfiguration :
    IEntityTypeConfiguration<MissionRevision>
{
    public void Configure(EntityTypeBuilder<MissionRevision> builder)
    {
        builder.ToTable("mission_revisions", DatabaseSchemas.Missions, table =>
        {
            table.HasCheckConstraint(
                "ck_mission_revisions_revision_number",
                "revision_number > 0");
            table.HasCheckConstraint(
                "ck_mission_revisions_target_count",
                "target_count > 0");
            table.HasCheckConstraint(
                "ck_mission_revisions_reward",
                "reward_bonus_coins BETWEEN 1 AND 300");
            table.HasCheckConstraint(
                "ck_mission_revisions_repeat_policy",
                "repeat_policy IN ('OncePerVietnamDate','UniqueSubjectPerPeriod')");
            table.HasCheckConstraint(
                "ck_mission_revisions_effective_window",
                "effective_until IS NULL OR effective_until > effective_from");
            table.HasCheckConstraint(
                "ck_mission_revisions_configuration_object",
                "jsonb_typeof(configuration_snapshot) = 'object'");
        });

        builder.HasKey(revision => revision.Id)
            .HasName("pk_mission_revisions");
        builder.HasAlternateKey(revision => new { revision.Id, revision.MissionId })
            .HasName("ak_mission_revisions_id_mission_id");
        builder.HasIndex(revision => new { revision.MissionId, revision.RevisionNumber })
            .IsUnique()
            .HasDatabaseName("ux_mission_revisions_mission_number");
        builder.HasIndex(revision => new
            {
                revision.MissionId,
                revision.EffectiveFrom,
                revision.EffectiveUntil,
            })
            .HasDatabaseName("ix_mission_revisions_effective_window");

        builder.Property(revision => revision.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(revision => revision.MissionId)
            .HasColumnName("mission_id");
        builder.Property(revision => revision.RevisionNumber)
            .HasColumnName("revision_number");
        builder.Property(revision => revision.TargetCount)
            .HasColumnName("target_count");
        builder.Property(revision => revision.RewardBonusCoins)
            .HasColumnName("reward_bonus_coins");
        builder.Property(revision => revision.RepeatPolicy)
            .HasColumnName("repeat_policy")
            .HasMaxLength(MissionRevision.MaximumRepeatPolicyLength)
            .IsRequired();
        builder.Property(revision => revision.EffectiveFrom)
            .HasColumnName("effective_from")
            .HasColumnType("timestamp with time zone");
        builder.Property(revision => revision.EffectiveUntil)
            .HasColumnName("effective_until")
            .HasColumnType("timestamp with time zone");
        builder.Property(revision => revision.ConfigurationSnapshot)
            .HasColumnName("configuration_snapshot")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasOne<MissionDefinition>()
            .WithMany()
            .HasForeignKey(revision => revision.MissionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_mission_revisions_definition");
    }
}
