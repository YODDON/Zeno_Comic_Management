using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicCatalogQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_comics_publication_updated_id",
                schema: "catalog",
                table: "comics",
                columns: new[] { "publication_status", "updated_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_chapters_public_comic_sort_id",
                schema: "catalog",
                table: "chapters",
                columns: new[] { "comic_id", "publication_status", "sort_order", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_comics_publication_updated_id",
                schema: "catalog",
                table: "comics");

            migrationBuilder.DropIndex(
                name: "ix_chapters_public_comic_sort_id",
                schema: "catalog",
                table: "chapters");
        }
    }
}
