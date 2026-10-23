using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Interaction.Ratings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ComicRatingConfiguration : IEntityTypeConfiguration<ComicRating>
{
    public void Configure(EntityTypeBuilder<ComicRating> builder)
    {
        builder.ToTable("comic_ratings", DatabaseSchemas.Interaction, table =>
        {
            table.HasCheckConstraint(
                "ck_comic_ratings_stars",
                "stars BETWEEN 1 AND 5");
            table.HasCheckConstraint(
                "ck_comic_ratings_timestamps",
                "updated_at >= created_at");
        });
        builder.HasKey(rating => new { rating.UserId, rating.ComicId })
            .HasName("pk_comic_ratings");
        builder.HasIndex(rating => rating.ComicId)
            .HasDatabaseName("ix_comic_ratings_comic_id");

        builder.Property(rating => rating.UserId).HasColumnName("user_id");
        builder.Property(rating => rating.ComicId).HasColumnName("comic_id");
        builder.Property(rating => rating.Stars).HasColumnName("stars");
        builder.Property(rating => rating.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(rating => rating.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(rating => rating.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_comic_ratings_users");
        builder.HasOne<Comic>()
            .WithMany()
            .HasForeignKey(rating => rating.ComicId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_comic_ratings_comics");
    }
}
