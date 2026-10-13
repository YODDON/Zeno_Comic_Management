using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterPublishedAtAndCatalogPagination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                schema: "catalog",
                table: "chapters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE catalog.chapters " +
                "SET published_at = updated_at " +
                "WHERE publication_status = 'Published' AND published_at IS NULL;");

            migrationBuilder.CreateIndex(
                name: "ix_chapters_public_comic_published_id",
                schema: "catalog",
                table: "chapters",
                columns: new[] { "comic_id", "publication_status", "published_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.AddCheckConstraint(
                name: "ck_chapters_published_at",
                schema: "catalog",
                table: "chapters",
                sql: "publication_status <> 'Published' OR published_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_chapters_public_comic_published_id",
                schema: "catalog",
                table: "chapters");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chapters_published_at",
                schema: "catalog",
                table: "chapters");

            migrationBuilder.DropColumn(
                name: "published_at",
                schema: "catalog",
                table: "chapters");
        }
    }
}
