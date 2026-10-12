using ComicManagement.Catalog.Comics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ComicConfiguration : IEntityTypeConfiguration<Comic>
{
    public void Configure(EntityTypeBuilder<Comic> builder)
    {
        builder.ToTable("comics", DatabaseSchemas.Catalog, table =>
        {
            table.HasCheckConstraint("ck_comics_slug_not_blank", "length(btrim(slug)) > 0");
            table.HasCheckConstraint("ck_comics_title_not_blank", "length(btrim(title)) > 0");
            table.HasCheckConstraint(
                "ck_comics_series_status",
                "series_status IN ('Ongoing', 'Completed', 'Hiatus')");
            table.HasCheckConstraint(
                "ck_comics_publication_status",
                "publication_status IN ('Draft', 'Published', 'Hidden')");
            table.HasCheckConstraint("ck_comics_default_price_coins", "default_price_coins >= 0");
            table.HasCheckConstraint("ck_comics_version", "version >= 1");
        });

        builder.HasKey(comic => comic.Id).HasName("pk_comics");
        builder.HasIndex(comic => comic.Slug).IsUnique().HasDatabaseName("ux_comics_slug");

        builder.Property(comic => comic.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(comic => comic.Slug)
            .HasColumnName("slug")
            .HasMaxLength(Comic.MaximumSlugLength)
            .IsRequired();
        builder.Property(comic => comic.Title).HasColumnName("title").HasColumnType("text").IsRequired();
        builder.Property(comic => comic.Description)
            .HasColumnName("description")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(comic => comic.SeriesStatus)
            .HasColumnName("series_status")
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(comic => comic.PublicationStatus)
            .HasColumnName("publication_status")
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(comic => comic.FirstPublishedAt)
            .HasColumnName("first_published_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(comic => comic.CoverAssetId)
            .HasColumnName("cover_asset_id")
            .HasColumnType("text");
        builder.Property(comic => comic.DefaultPriceCoins).HasColumnName("default_price_coins");
        builder.Property(comic => comic.DefaultVipEligible).HasColumnName("default_vip_eligible");
        builder.Property(comic => comic.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(comic => comic.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(comic => comic.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone");
    }
}
