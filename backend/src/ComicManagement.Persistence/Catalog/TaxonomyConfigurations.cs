using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class GenreConfiguration : IEntityTypeConfiguration<Genre>
{
    public void Configure(EntityTypeBuilder<Genre> builder)
    {
        builder.ToTable("genres", DatabaseSchemas.Catalog, table =>
        {
            table.HasCheckConstraint("ck_genres_slug_not_blank", "length(btrim(slug)) > 0");
            table.HasCheckConstraint("ck_genres_name_not_blank", "length(btrim(name)) > 0");
        });
        builder.HasKey(genre => genre.Id).HasName("pk_genres");
        builder.HasIndex(genre => genre.Slug).IsUnique().HasDatabaseName("ux_genres_slug");
        builder.Property(genre => genre.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(genre => genre.Slug)
            .HasColumnName("slug")
            .HasMaxLength(Genre.MaximumSlugLength)
            .IsRequired();
        builder.Property(genre => genre.Name).HasColumnName("name").HasColumnType("text").IsRequired();
    }
}

internal sealed class AuthorConfiguration : IEntityTypeConfiguration<Author>
{
    public void Configure(EntityTypeBuilder<Author> builder)
    {
        builder.ToTable("authors", DatabaseSchemas.Catalog, table =>
            table.HasCheckConstraint("ck_authors_name_not_blank", "length(btrim(name)) > 0"));
        builder.HasKey(author => author.Id).HasName("pk_authors");
        builder.Property(author => author.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(author => author.Name).HasColumnName("name").HasColumnType("text").IsRequired();
    }
}

internal sealed class TranslationGroupConfiguration : IEntityTypeConfiguration<TranslationGroup>
{
    public void Configure(EntityTypeBuilder<TranslationGroup> builder)
    {
        builder.ToTable("translation_groups", DatabaseSchemas.Catalog, table =>
            table.HasCheckConstraint(
                "ck_translation_groups_name_not_blank",
                "length(btrim(name)) > 0"));
        builder.HasKey(group => group.Id).HasName("pk_translation_groups");
        builder.Property(group => group.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(group => group.Name).HasColumnName("name").HasColumnType("text").IsRequired();
    }
}

internal sealed class ComicGenreConfiguration : IEntityTypeConfiguration<ComicGenre>
{
    public void Configure(EntityTypeBuilder<ComicGenre> builder)
    {
        builder.ToTable("comic_genres", DatabaseSchemas.Catalog);
        builder.HasKey(link => new { link.ComicId, link.GenreId }).HasName("pk_comic_genres");
        builder.HasIndex(link => link.GenreId).HasDatabaseName("ix_comic_genres_genre_id");
        builder.Property(link => link.ComicId).HasColumnName("comic_id");
        builder.Property(link => link.GenreId).HasColumnName("genre_id");
        builder.HasOne<Comic>().WithMany().HasForeignKey(link => link.ComicId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_comic_genres_comics");
        builder.HasOne<Genre>().WithMany().HasForeignKey(link => link.GenreId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_comic_genres_genres");
    }
}

internal sealed class ComicAuthorConfiguration : IEntityTypeConfiguration<ComicAuthor>
{
    public void Configure(EntityTypeBuilder<ComicAuthor> builder)
    {
        builder.ToTable("comic_authors", DatabaseSchemas.Catalog);
        builder.HasKey(link => new { link.ComicId, link.AuthorId }).HasName("pk_comic_authors");
        builder.HasIndex(link => link.AuthorId).HasDatabaseName("ix_comic_authors_author_id");
        builder.Property(link => link.ComicId).HasColumnName("comic_id");
        builder.Property(link => link.AuthorId).HasColumnName("author_id");
        builder.HasOne<Comic>().WithMany().HasForeignKey(link => link.ComicId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_comic_authors_comics");
        builder.HasOne<Author>().WithMany().HasForeignKey(link => link.AuthorId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_comic_authors_authors");
    }
}

internal sealed class ComicTranslationGroupConfiguration : IEntityTypeConfiguration<ComicTranslationGroup>
{
    public void Configure(EntityTypeBuilder<ComicTranslationGroup> builder)
    {
        builder.ToTable("comic_translation_groups", DatabaseSchemas.Catalog);
        builder.HasKey(link => new { link.ComicId, link.TranslationGroupId })
            .HasName("pk_comic_translation_groups");
        builder.HasIndex(link => link.TranslationGroupId)
            .HasDatabaseName("ix_comic_translation_groups_translation_group_id");
        builder.Property(link => link.ComicId).HasColumnName("comic_id");
        builder.Property(link => link.TranslationGroupId).HasColumnName("translation_group_id");
        builder.HasOne<Comic>().WithMany().HasForeignKey(link => link.ComicId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_comic_translation_groups_comics");
        builder.HasOne<TranslationGroup>().WithMany().HasForeignKey(link => link.TranslationGroupId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_comic_translation_groups_translation_groups");
    }
}
