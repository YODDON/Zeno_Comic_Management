using ComicManagement.Missions.Definitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class MissionActivationConfiguration : IEntityTypeConfiguration<MissionActivation>
{
    public void Configure(EntityTypeBuilder<MissionActivation> builder)
    {
        builder.ToTable("mission_activations", DatabaseSchemas.Missions, table =>
        {
            table.HasCheckConstraint(
                "ck_mission_activations_sequence",
                "activation_sequence >= 1");
            table.HasCheckConstraint(
                "ck_mission_activations_window",
                "deactivated_at IS NULL OR deactivated_at >= activated_at");
        });

        builder.HasKey(item => item.Id).HasName("pk_mission_activations");
        builder.HasIndex(item => new { item.MissionDefinitionId, item.ActivationSequence })
            .IsUnique()
            .HasDatabaseName("ux_mission_activations_mission_sequence");
        builder.HasIndex(item => item.MissionDefinitionId)
            .IsUnique()
            .HasFilter("deactivated_at IS NULL")
            .HasDatabaseName("ux_mission_activations_open");
        builder.HasIndex(item => new { item.MissionDefinitionId, item.ActivatedAt })
            .HasDatabaseName("ix_mission_activations_history");
        builder.HasIndex(item => new { item.MissionRevisionId, item.MissionDefinitionId })
            .HasDatabaseName("ix_mission_activations_revision_owner");

        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.MissionDefinitionId).HasColumnName("mission_definition_id");
        builder.Property(item => item.MissionRevisionId).HasColumnName("mission_revision_id");
        builder.Property(item => item.ActivationSequence).HasColumnName("activation_sequence");
        builder.Property(item => item.ActivatedAt)
            .HasColumnName("activated_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.DeactivatedAt)
            .HasColumnName("deactivated_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<MissionRevision>()
            .WithMany()
            .HasForeignKey(item => new { item.MissionRevisionId, item.MissionDefinitionId })
            .HasPrincipalKey(revision => new { revision.Id, revision.MissionId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_mission_activations_revision_owner");
    }
}
