using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ChapterConfiguration : IEntityTypeConfiguration<Chapter>
{
    public void Configure(EntityTypeBuilder<Chapter> builder)
    {
        builder.ToTable("chapters", DatabaseSchemas.Catalog, table =>
        {
            table.HasCheckConstraint("ck_chapters_label_not_blank", "length(btrim(chapter_label)) > 0");
            table.HasCheckConstraint(
                "ck_chapters_price_override_coins",
                "price_override_coins IS NULL OR price_override_coins >= 0");
            table.HasCheckConstraint(
                "ck_chapters_publication_status",
                "publication_status IN ('Draft', 'Published', 'Hidden')");
            table.HasCheckConstraint("ck_chapters_version", "version >= 1");
        });

        builder.HasKey(chapter => chapter.Id).HasName("pk_chapters");
        builder.HasIndex(chapter => new { chapter.ComicId, chapter.SortOrder })
            .IsUnique()
            .HasDatabaseName("ux_chapters_comic_id_sort_order");
        builder.HasIndex(chapter => new
        {
            chapter.ComicId,
            chapter.PublicationStatus,
            chapter.SortOrder,
            chapter.Id,
        })
            .HasDatabaseName("ix_chapters_public_comic_sort_id");

        builder.Property(chapter => chapter.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(chapter => chapter.ComicId).HasColumnName("comic_id");
        builder.Property(chapter => chapter.ChapterLabel)
            .HasColumnName("chapter_label")
            .HasMaxLength(Chapter.MaximumLabelLength)
            .IsRequired();
        builder.Property(chapter => chapter.SortOrder).HasColumnName("sort_order");
        builder.Property(chapter => chapter.PriceOverrideCoins).HasColumnName("price_override_coins");
        builder.Property(chapter => chapter.VipOverride).HasColumnName("vip_override");
        builder.Property(chapter => chapter.PublicationStatus)
            .HasColumnName("publication_status")
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(chapter => chapter.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(chapter => chapter.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(chapter => chapter.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<Comic>()
            .WithMany()
            .HasForeignKey(chapter => chapter.ComicId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_chapters_comics");
    }
}

internal sealed class ChapterPageConfiguration : IEntityTypeConfiguration<ChapterPage>
{
    public void Configure(EntityTypeBuilder<ChapterPage> builder)
    {
        builder.ToTable("chapter_pages", DatabaseSchemas.Catalog, table =>
        {
            table.HasCheckConstraint(
                "ck_chapter_pages_cloudinary_public_id_not_blank",
                "length(btrim(cloudinary_public_id)) > 0");
            table.HasCheckConstraint(
                "ck_chapter_pages_asset_status",
                "asset_status IN ('Pending', 'Ready')");
            table.HasCheckConstraint(
                "ck_chapter_pages_upload_fingerprint",
                "upload_fingerprint ~ '^(sha256:[0-9a-f]{64}|legacy:[0-9a-f-]{36})$'");
            table.HasCheckConstraint(
                "ck_chapter_pages_asset_dimensions",
                "(asset_status = 'Pending' AND width = 0 AND height = 0) " +
                "OR (asset_status = 'Ready' AND width > 0 AND height > 0)");
        });

        builder.HasKey(page => page.Id).HasName("pk_chapter_pages");
        builder.HasIndex(page => new { page.ChapterId, page.PageOrder })
            .IsUnique()
            .HasDatabaseName("ux_chapter_pages_chapter_id_page_order");
        builder.HasIndex(page => page.CloudinaryPublicId)
            .IsUnique()
            .HasDatabaseName("ux_chapter_pages_cloudinary_public_id");
        builder.HasIndex(page => new { page.AssetStatus, page.UploadCreatedAt })
            .HasDatabaseName("ix_chapter_pages_asset_status_upload_created_at");

        builder.Property(page => page.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(page => page.ChapterId).HasColumnName("chapter_id");
        builder.Property(page => page.PageOrder).HasColumnName("page_order");
        builder.Property(page => page.CloudinaryPublicId)
            .HasColumnName("cloudinary_public_id")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(page => page.AssetStatus)
            .HasColumnName("asset_status")
            .HasMaxLength(ChapterPage.MaximumAssetStatusLength)
            .IsRequired();
        builder.Property(page => page.UploadFingerprint)
            .HasColumnName("upload_fingerprint")
            .HasMaxLength(ChapterPage.MaximumUploadFingerprintLength)
            .IsRequired();
        builder.Property(page => page.UploadCreatedAt)
            .HasColumnName("upload_created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(page => page.Width).HasColumnName("width");
        builder.Property(page => page.Height).HasColumnName("height");

        builder.HasOne<Chapter>()
            .WithMany()
            .HasForeignKey(page => page.ChapterId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_chapter_pages_chapters");
    }
}
