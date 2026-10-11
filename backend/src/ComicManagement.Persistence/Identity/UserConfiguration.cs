using ComicManagement.Identity.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", DatabaseSchemas.Identity, table =>
        {
            table.HasCheckConstraint(
                "ck_users_display_name_not_blank",
                "length(btrim(display_name)) > 0");
            table.HasCheckConstraint(
                "ck_users_role",
                "role IN ('User', 'Admin')");
            table.HasCheckConstraint(
                "ck_users_status",
                "status IN ('Active', 'Disabled')");
            table.HasCheckConstraint(
                "ck_users_version",
                "version >= 1");
        });

        builder.HasKey(user => user.Id)
            .HasName("pk_users");

        builder.Property(user => user.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(user => user.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(User.MaximumDisplayNameLength)
            .IsRequired();
        builder.Property(user => user.Email)
            .HasColumnName("email")
            .HasMaxLength(User.MaximumEmailLength);
        builder.Property(user => user.AvatarUrl)
            .HasColumnName("avatar_url");
        builder.Property(user => user.Role)
            .HasColumnName("role")
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(user => user.Status)
            .HasColumnName("status")
            .HasMaxLength(24)
            .IsRequired();
        builder.Property(user => user.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(user => user.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(user => user.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
    }
}
