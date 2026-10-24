using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Interaction.Comments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ToTable("comments", DatabaseSchemas.Interaction, table =>
        {
            table.HasCheckConstraint("ck_comments_exactly_one_target", "(comic_id IS NULL) <> (chapter_id IS NULL)");
            table.HasCheckConstraint("ck_comments_target_not_empty", "(comic_id IS NULL OR comic_id <> '00000000-0000-0000-0000-000000000000') AND (chapter_id IS NULL OR chapter_id <> '00000000-0000-0000-0000-000000000000')");
            table.HasCheckConstraint("ck_comments_parent_not_self", "parent_id IS NULL OR parent_id <> id");
            table.HasCheckConstraint("ck_comments_body_not_blank", "length(btrim(body)) > 0");
            table.HasCheckConstraint("ck_comments_status", "status IN ('Visible','Hidden','Deleted')");
            table.HasCheckConstraint("ck_comments_timestamps", "updated_at >= created_at");
            table.HasCheckConstraint("ck_comments_version", "version >= 1");
        });
        builder.HasKey(comment => comment.Id).HasName("pk_comments");
        builder.HasIndex(comment => new { comment.ComicId, comment.ParentId, comment.CreatedAt, comment.Id })
            .HasDatabaseName("ix_comments_comic_parent_created_id");
        builder.HasIndex(comment => new { comment.ChapterId, comment.ParentId, comment.CreatedAt, comment.Id })
            .HasDatabaseName("ix_comments_chapter_parent_created_id");
        builder.HasIndex(comment => new { comment.ParentId, comment.CreatedAt, comment.Id })
            .HasDatabaseName("ix_comments_parent_created_id");
        builder.HasIndex(comment => comment.UserId)
            .HasDatabaseName("ix_comments_user_id");
        builder.HasIndex(comment => new { comment.Status, comment.CreatedAt, comment.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_comments_status_created_id");
        builder.HasIndex(comment => new { comment.CreatedAt, comment.Id })
            .IsDescending(true, true)
            .HasDatabaseName("ix_comments_created_id");

        builder.Property(comment => comment.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(comment => comment.UserId).HasColumnName("user_id");
        builder.Property(comment => comment.ComicId).HasColumnName("comic_id");
        builder.Property(comment => comment.ChapterId).HasColumnName("chapter_id");
        builder.Property(comment => comment.ParentId).HasColumnName("parent_id");
        builder.Property(comment => comment.Body).HasColumnName("body").HasMaxLength(Comment.MaximumBodyLength).IsRequired();
        builder.Property(comment => comment.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        builder.Property(comment => comment.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(comment => comment.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        builder.Property(comment => comment.Version).HasColumnName("version").IsConcurrencyToken();

        builder.HasOne<User>().WithMany().HasForeignKey(comment => comment.UserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_comments_users");
        builder.HasOne<Comic>().WithMany().HasForeignKey(comment => comment.ComicId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_comments_comics");
        builder.HasOne<Chapter>().WithMany().HasForeignKey(comment => comment.ChapterId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_comments_chapters");
        builder.HasOne<Comment>().WithMany().HasForeignKey(comment => comment.ParentId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_comments_parent");
    }
}
