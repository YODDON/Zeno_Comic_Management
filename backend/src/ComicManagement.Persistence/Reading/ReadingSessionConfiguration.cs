using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Reading.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class ReadingSessionConfiguration : IEntityTypeConfiguration<ReadingSession>
{
    public void Configure(EntityTypeBuilder<ReadingSession> builder)
    {
        builder.ToTable("reading_sessions", DatabaseSchemas.Reading, table =>
        {
            table.HasCheckConstraint(
                "ck_reading_sessions_expiry",
                "expires_at = started_at + interval '30 minutes'");
            table.HasCheckConstraint(
                "ck_reading_sessions_active_duration",
                "active_duration_ms BETWEEN 0 AND 1800000");
            table.HasCheckConstraint(
                "ck_reading_sessions_current_position",
                "(current_page_order IS NULL AND current_page_offset_ratio IS NULL) OR " +
                "(current_page_order >= 1 AND current_page_offset_ratio BETWEEN 0 AND 1)");
            table.HasCheckConstraint(
                "ck_reading_sessions_progress",
                "max_progress_basis_points BETWEEN 0 AND 10000");
            table.HasCheckConstraint(
                "ck_reading_sessions_manifest",
                "manifest_revision ~ '^[0-9a-f]{64}$' AND manifest_page_count >= 1");
            table.HasCheckConstraint(
                "ck_reading_sessions_sequence",
                "(last_accepted_sequence = 0 AND last_request_fingerprint IS NULL) OR " +
                "(last_accepted_sequence >= 1 AND last_request_fingerprint ~ '^[0-9a-f]{64}$')");
            table.HasCheckConstraint(
                "ck_reading_sessions_policy",
                "minimum_progress_basis_points BETWEEN 1 AND 10000 AND " +
                "minimum_active_duration_ms >= 1 AND length(btrim(policy_revision)) > 0");
            table.HasCheckConstraint(
                "ck_reading_sessions_status",
                "status IN ('Active', 'Qualified')");
            table.HasCheckConstraint(
                "ck_reading_sessions_qualification",
                "(status = 'Active' AND qualified_at IS NULL) OR " +
                "(status = 'Qualified' AND qualified_at IS NOT NULL AND " +
                "max_progress_basis_points >= minimum_progress_basis_points AND " +
                "active_duration_ms >= minimum_active_duration_ms AND qualified_at < expires_at)");
            table.HasCheckConstraint("ck_reading_sessions_version", "version >= 1");
        });

        builder.HasKey(session => session.Id).HasName("pk_reading_sessions");
        builder.HasIndex(session => new { session.UserId, session.StartedAt, session.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_reading_sessions_user_started_id");
        builder.HasIndex(session => new { session.ChapterId, session.QualifiedAt })
            .HasDatabaseName("ix_reading_sessions_chapter_qualified_at");
        builder.HasIndex(session => session.ComicId)
            .HasDatabaseName("ix_reading_sessions_comic_id");

        builder.Property(session => session.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(session => session.UserId).HasColumnName("user_id");
        builder.Property(session => session.ComicId).HasColumnName("comic_id");
        builder.Property(session => session.ChapterId).HasColumnName("chapter_id");
        builder.Property(session => session.StartedAt)
            .HasColumnName("started_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(session => session.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(session => session.LastActivityAt)
            .HasColumnName("last_activity_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(session => session.LastActivityWasReady)
            .HasColumnName("last_activity_was_ready");
        builder.Property(session => session.ActiveDurationMs)
            .HasColumnName("active_duration_ms");
        builder.Property(session => session.CurrentPageOrder)
            .HasColumnName("current_page_order");
        builder.Property(session => session.CurrentPageOffsetRatio)
            .HasColumnName("current_page_offset_ratio")
            .HasColumnType("numeric");
        builder.Property(session => session.MaxProgressBasisPoints)
            .HasColumnName("max_progress_basis_points");
        builder.Property(session => session.ManifestRevision)
            .HasColumnName("manifest_revision")
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(session => session.ManifestPageCount)
            .HasColumnName("manifest_page_count");
        builder.Property(session => session.LastAcceptedSequence)
            .HasColumnName("last_accepted_sequence");
        builder.Property(session => session.LastRequestFingerprint)
            .HasColumnName("last_request_fingerprint")
            .HasMaxLength(64);
        builder.Property(session => session.MinimumProgressBasisPoints)
            .HasColumnName("minimum_progress_basis_points");
        builder.Property(session => session.MinimumActiveDurationMs)
            .HasColumnName("minimum_active_duration_ms");
        builder.Property(session => session.PolicyRevision)
            .HasColumnName("policy_revision")
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(session => session.QualifiedAt)
            .HasColumnName("qualified_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(session => session.Status)
            .HasColumnName("status")
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(session => session.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reading_sessions_users");
        builder.HasOne<Comic>()
            .WithMany()
            .HasForeignKey(session => session.ComicId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reading_sessions_comics");
        builder.HasOne<Chapter>()
            .WithMany()
            .HasForeignKey(session => session.ChapterId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reading_sessions_chapters");
    }
}

internal sealed class ReadingProgressConfiguration : IEntityTypeConfiguration<ReadingProgress>
{
    public void Configure(EntityTypeBuilder<ReadingProgress> builder)
    {
        builder.ToTable("reading_progress", DatabaseSchemas.Reading, table =>
        {
            table.HasCheckConstraint(
                "ck_reading_progress_page_order",
                "last_page_order >= 1");
            table.HasCheckConstraint(
                "ck_reading_progress_offset",
                "last_page_offset_ratio BETWEEN 0 AND 1");
            table.HasCheckConstraint("ck_reading_progress_version", "version >= 1");
        });

        builder.HasKey(progress => new { progress.UserId, progress.ComicId })
            .HasName("pk_reading_progress");
        builder.HasIndex(progress => new { progress.UserId, progress.LastReadAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_reading_progress_user_last_read_at");
        builder.HasIndex(progress => progress.ComicId)
            .HasDatabaseName("ix_reading_progress_comic_id");
        builder.HasIndex(progress => progress.LastChapterId)
            .HasDatabaseName("ix_reading_progress_last_chapter_id");
        builder.HasIndex(progress => progress.SourceSessionId)
            .HasDatabaseName("ix_reading_progress_source_session_id");

        builder.Property(progress => progress.UserId).HasColumnName("user_id");
        builder.Property(progress => progress.ComicId).HasColumnName("comic_id");
        builder.Property(progress => progress.LastChapterId).HasColumnName("last_chapter_id");
        builder.Property(progress => progress.LastPageOrder).HasColumnName("last_page_order");
        builder.Property(progress => progress.LastPageOffsetRatio)
            .HasColumnName("last_page_offset_ratio")
            .HasColumnType("numeric");
        builder.Property(progress => progress.LastReadAt)
            .HasColumnName("last_read_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(progress => progress.SourceSessionId)
            .HasColumnName("source_session_id");
        builder.Property(progress => progress.SourceSessionStartedAt)
            .HasColumnName("source_session_started_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(progress => progress.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(progress => progress.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reading_progress_users");
        builder.HasOne<Comic>()
            .WithMany()
            .HasForeignKey(progress => progress.ComicId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reading_progress_comics");
        builder.HasOne<Chapter>()
            .WithMany()
            .HasForeignKey(progress => progress.LastChapterId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reading_progress_chapters");
        builder.HasOne<ReadingSession>()
            .WithMany()
            .HasForeignKey(progress => progress.SourceSessionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reading_progress_source_session");
    }
}
