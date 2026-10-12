using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DefineChapterPageAssetLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_chapter_pages_asset_status_not_blank",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chapter_pages_height",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chapter_pages_width",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.CreateIndex(
                name: "ux_chapter_pages_cloudinary_public_id",
                schema: "catalog",
                table: "chapter_pages",
                column: "cloudinary_public_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_chapter_pages_asset_dimensions",
                schema: "catalog",
                table: "chapter_pages",
                sql: "(asset_status = 'Pending' AND width = 0 AND height = 0) OR (asset_status = 'Ready' AND width > 0 AND height > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chapter_pages_asset_status",
                schema: "catalog",
                table: "chapter_pages",
                sql: "asset_status IN ('Pending', 'Ready')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_chapter_pages_cloudinary_public_id",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chapter_pages_asset_dimensions",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chapter_pages_asset_status",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chapter_pages_asset_status_not_blank",
                schema: "catalog",
                table: "chapter_pages",
                sql: "length(btrim(asset_status)) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chapter_pages_height",
                schema: "catalog",
                table: "chapter_pages",
                sql: "height >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chapter_pages_width",
                schema: "catalog",
                table: "chapter_pages",
                sql: "width >= 0");
        }
    }
}
