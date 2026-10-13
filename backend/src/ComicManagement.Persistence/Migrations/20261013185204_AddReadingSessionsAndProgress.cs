using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReadingSessionsAndProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reading");

            migrationBuilder.CreateTable(
                name: "reading_sessions",
                schema: "reading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chapter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_activity_was_ready = table.Column<bool>(type: "boolean", nullable: false),
                    active_duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    current_page_order = table.Column<int>(type: "integer", nullable: true),
                    current_page_offset_ratio = table.Column<decimal>(type: "numeric", nullable: true),
                    max_progress_basis_points = table.Column<int>(type: "integer", nullable: false),
                    manifest_revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    manifest_page_count = table.Column<int>(type: "integer", nullable: false),
                    last_accepted_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    minimum_progress_basis_points = table.Column<int>(type: "integer", nullable: false),
                    minimum_active_duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    policy_revision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    qualified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reading_sessions", x => x.id);
                    table.CheckConstraint("ck_reading_sessions_active_duration", "active_duration_ms BETWEEN 0 AND 1800000");
                    table.CheckConstraint("ck_reading_sessions_current_position", "(current_page_order IS NULL AND current_page_offset_ratio IS NULL) OR (current_page_order >= 1 AND current_page_offset_ratio BETWEEN 0 AND 1)");
                    table.CheckConstraint("ck_reading_sessions_expiry", "expires_at = started_at + interval '30 minutes'");
                    table.CheckConstraint("ck_reading_sessions_manifest", "manifest_revision ~ '^[0-9a-f]{64}$' AND manifest_page_count >= 1");
                    table.CheckConstraint("ck_reading_sessions_policy", "minimum_progress_basis_points BETWEEN 1 AND 10000 AND minimum_active_duration_ms >= 1 AND length(btrim(policy_revision)) > 0");
                    table.CheckConstraint("ck_reading_sessions_progress", "max_progress_basis_points BETWEEN 0 AND 10000");
                    table.CheckConstraint("ck_reading_sessions_qualification", "(status = 'Active' AND qualified_at IS NULL) OR (status = 'Qualified' AND qualified_at IS NOT NULL AND max_progress_basis_points >= minimum_progress_basis_points AND active_duration_ms >= minimum_active_duration_ms AND qualified_at < expires_at)");
                    table.CheckConstraint("ck_reading_sessions_sequence", "(last_accepted_sequence = 0 AND last_request_fingerprint IS NULL) OR (last_accepted_sequence >= 1 AND last_request_fingerprint ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_reading_sessions_status", "status IN ('Active', 'Qualified')");
                    table.CheckConstraint("ck_reading_sessions_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_reading_sessions_chapters",
                        column: x => x.chapter_id,
                        principalSchema: "catalog",
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reading_sessions_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reading_sessions_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reading_progress",
                schema: "reading",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_chapter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_page_order = table.Column<int>(type: "integer", nullable: false),
                    last_page_offset_ratio = table.Column<decimal>(type: "numeric", nullable: false),
                    last_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_session_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reading_progress", x => new { x.user_id, x.comic_id });
                    table.CheckConstraint("ck_reading_progress_offset", "last_page_offset_ratio BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_reading_progress_page_order", "last_page_order >= 1");
                    table.CheckConstraint("ck_reading_progress_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_reading_progress_chapters",
                        column: x => x.last_chapter_id,
                        principalSchema: "catalog",
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reading_progress_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reading_progress_source_session",
                        column: x => x.source_session_id,
                        principalSchema: "reading",
                        principalTable: "reading_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reading_progress_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reading_progress_comic_id",
                schema: "reading",
                table: "reading_progress",
                column: "comic_id");

            migrationBuilder.CreateIndex(
                name: "ix_reading_progress_last_chapter_id",
                schema: "reading",
                table: "reading_progress",
                column: "last_chapter_id");

            migrationBuilder.CreateIndex(
                name: "ix_reading_progress_source_session_id",
                schema: "reading",
                table: "reading_progress",
                column: "source_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_reading_progress_user_last_read_at",
                schema: "reading",
                table: "reading_progress",
                columns: new[] { "user_id", "last_read_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_reading_sessions_chapter_qualified_at",
                schema: "reading",
                table: "reading_sessions",
                columns: new[] { "chapter_id", "qualified_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reading_sessions_comic_id",
                schema: "reading",
                table: "reading_sessions",
                column: "comic_id");

            migrationBuilder.CreateIndex(
                name: "ix_reading_sessions_user_started_id",
                schema: "reading",
                table: "reading_sessions",
                columns: new[] { "user_id", "started_at", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reading_progress",
                schema: "reading");

            migrationBuilder.DropTable(
                name: "reading_sessions",
                schema: "reading");
        }
    }
}
