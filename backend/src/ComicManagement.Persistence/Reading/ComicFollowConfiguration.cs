using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Reading.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ComicFollowConfiguration : IEntityTypeConfiguration<ComicFollow>
{
    public void Configure(EntityTypeBuilder<ComicFollow> builder)
    {
        builder.ToTable("comic_follows", DatabaseSchemas.Reading);
        builder.HasKey(follow => new { follow.UserId, follow.ComicId })
            .HasName("pk_comic_follows");
        builder.HasIndex(follow => new { follow.UserId, follow.FollowedAt, follow.ComicId })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_comic_follows_user_followed_comic");
        builder.HasIndex(follow => follow.ComicId)
            .HasDatabaseName("ix_comic_follows_comic_id");

        builder.Property(follow => follow.UserId).HasColumnName("user_id");
        builder.Property(follow => follow.ComicId).HasColumnName("comic_id");
        builder.Property(follow => follow.FollowedAt)
            .HasColumnName("followed_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(follow => follow.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_comic_follows_users");
        builder.HasOne<Comic>()
            .WithMany()
            .HasForeignKey(follow => follow.ComicId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_comic_follows_comics");
    }
}
