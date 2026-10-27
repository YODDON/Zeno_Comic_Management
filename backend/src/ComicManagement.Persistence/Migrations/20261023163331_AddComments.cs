using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comments",
                schema: "interaction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comic_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chapter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.CheckConstraint("ck_comments_body_not_blank", "length(btrim(body)) > 0");
                    table.CheckConstraint("ck_comments_exactly_one_target", "(comic_id IS NULL) <> (chapter_id IS NULL)");
                    table.CheckConstraint("ck_comments_parent_not_self", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_comments_status", "status IN ('Visible','Hidden','Deleted')");
                    table.CheckConstraint("ck_comments_target_not_empty", "(comic_id IS NULL OR comic_id <> '00000000-0000-0000-0000-000000000000') AND (chapter_id IS NULL OR chapter_id <> '00000000-0000-0000-0000-000000000000')");
                    table.CheckConstraint("ck_comments_timestamps", "updated_at >= created_at");
                    table.CheckConstraint("ck_comments_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_comments_chapters",
                        column: x => x.chapter_id,
                        principalSchema: "catalog",
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comments_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comments_parent",
                        column: x => x.parent_id,
                        principalSchema: "interaction",
                        principalTable: "comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comments_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comments_chapter_parent_created_id",
                schema: "interaction",
                table: "comments",
                columns: new[] { "chapter_id", "parent_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_comic_parent_created_id",
                schema: "interaction",
                table: "comments",
                columns: new[] { "comic_id", "parent_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_parent_created_id",
                schema: "interaction",
                table: "comments",
                columns: new[] { "parent_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_created_id",
                schema: "interaction",
                table: "comments",
                columns: new[] { "created_at", "id" },
                descending: new[] { true, true });

            migrationBuilder.CreateIndex(
                name: "ix_comments_status_created_id",
                schema: "interaction",
                table: "comments",
                columns: new[] { "status", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_comments_user_id",
                schema: "interaction",
                table: "comments",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comments",
                schema: "interaction");
        }
    }
}
