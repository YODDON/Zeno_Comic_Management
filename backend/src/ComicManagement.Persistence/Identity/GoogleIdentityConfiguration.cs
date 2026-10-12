using ComicManagement.Identity.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class GoogleIdentityConfiguration : IEntityTypeConfiguration<GoogleIdentity>
{
    public void Configure(EntityTypeBuilder<GoogleIdentity> builder)
    {
        builder.ToTable("google_identities", DatabaseSchemas.Identity, table =>
        {
            table.HasCheckConstraint(
                "ck_google_identities_google_subject_not_blank",
                "length(btrim(google_subject)) > 0");
        });

        builder.HasKey(identity => identity.UserId)
            .HasName("pk_google_identities");

        builder.Property(identity => identity.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();
        builder.Property(identity => identity.GoogleSubject)
            .HasColumnName("google_subject")
            .HasMaxLength(GoogleIdentity.MaximumSubjectLength)
            .IsRequired();
        builder.Property(identity => identity.LinkedAt)
            .HasColumnName("linked_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(identity => identity.GoogleSubject)
            .IsUnique()
            .HasDatabaseName("uq_google_identities_google_subject");

        builder.HasOne(identity => identity.User)
            .WithOne()
            .HasForeignKey<GoogleIdentity>(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_google_identities_users_user_id");
    }
}
