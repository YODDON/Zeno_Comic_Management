using ComicManagement.Missions.Definitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class MissionDefinitionConfiguration :
    IEntityTypeConfiguration<MissionDefinition>
{
    public void Configure(EntityTypeBuilder<MissionDefinition> builder)
    {
        builder.ToTable("mission_definitions", DatabaseSchemas.Missions, table =>
        {
            table.HasCheckConstraint(
                "ck_mission_definitions_action_type",
                "action_type IN ('DailyLogin','ChapterReadQualified','ComicFollowed'," +
                "'ChapterUnlocked','WalletTopUpCompleted')");
            table.HasCheckConstraint(
                "ck_mission_definitions_cycle_type",
                "cycle_type IN ('Fixed','Daily','Weekly','Event')");
            table.HasCheckConstraint(
                "ck_mission_definitions_status",
                "status IN ('Draft','Active')");
            table.HasCheckConstraint(
                "ck_mission_definitions_version",
                "version >= 1");
            table.HasCheckConstraint(
                "ck_mission_definitions_active_revision",
                "(status = 'Draft' AND active_revision_id IS NULL) OR " +
                "(status = 'Active' AND active_revision_id IS NOT NULL)");
        });

        builder.HasKey(definition => definition.Id)
            .HasName("pk_mission_definitions");
        builder.HasIndex(definition => definition.Code)
            .IsUnique()
            .HasDatabaseName("ux_mission_definitions_code");

        builder.Property(definition => definition.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(definition => definition.Code)
            .HasColumnName("code")
            .HasMaxLength(MissionDefinition.MaximumCodeLength)
            .IsRequired();
        builder.Property(definition => definition.Name)
            .HasColumnName("name")
            .HasMaxLength(MissionDefinition.MaximumNameLength)
            .IsRequired();
        builder.Property(definition => definition.Description)
            .HasColumnName("description")
            .HasMaxLength(MissionDefinition.MaximumDescriptionLength)
            .IsRequired();
        builder.Property(definition => definition.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(40)
            .IsRequired();
        builder.Property(definition => definition.CycleType)
            .HasColumnName("cycle_type")
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(definition => definition.Status)
            .HasColumnName("status")
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(definition => definition.ActiveRevisionId)
            .HasColumnName("active_revision_id");
        builder.Property(definition => definition.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(definition => definition.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasOne<MissionRevision>()
            .WithMany()
            .HasForeignKey(definition => definition.ActiveRevisionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_mission_definitions_active_revision");
    }
}
