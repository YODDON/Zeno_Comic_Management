using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Featured;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class FeaturedComicConfiguration : IEntityTypeConfiguration<FeaturedComic>
{
    public void Configure(EntityTypeBuilder<FeaturedComic> builder)
    {
        builder.ToTable("featured_comics", DatabaseSchemas.Catalog, t =>
            t.HasCheckConstraint("ck_featured_comics_display_order", "display_order BETWEEN 1 AND 5"));
        builder.HasKey(x => x.ComicId).HasName("pk_featured_comics");
        builder.HasIndex(x => x.DisplayOrder).IsUnique().HasDatabaseName("ux_featured_comics_display_order");
        builder.Property(x => x.ComicId).HasColumnName("comic_id").ValueGeneratedNever();
        builder.Property(x => x.DisplayOrder).HasColumnName("display_order");
        builder.HasOne<Comic>().WithMany().HasForeignKey(x => x.ComicId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_featured_comics_comics");
    }
}

internal sealed class FeaturedConfigurationConfiguration : IEntityTypeConfiguration<FeaturedConfiguration>
{
    public void Configure(EntityTypeBuilder<FeaturedConfiguration> builder)
    {
        builder.ToTable("featured_configuration", DatabaseSchemas.Catalog, t =>
        {
            t.HasCheckConstraint("ck_featured_configuration_singleton", "id = 1");
            t.HasCheckConstraint("ck_featured_configuration_version", "version >= 1");
        });
        builder.HasKey(x => x.Id).HasName("pk_featured_configuration");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
    }
}
