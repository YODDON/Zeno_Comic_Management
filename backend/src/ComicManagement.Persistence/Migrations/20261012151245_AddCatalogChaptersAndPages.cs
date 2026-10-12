using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogChaptersAndPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chapters",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    comic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chapter_label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    price_override_coins = table.Column<long>(type: "bigint", nullable: true),
                    vip_override = table.Column<bool>(type: "boolean", nullable: true),
                    publication_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chapters", x => x.id);
                    table.CheckConstraint("ck_chapters_label_not_blank", "length(btrim(chapter_label)) > 0");
                    table.CheckConstraint("ck_chapters_price_override_coins", "price_override_coins IS NULL OR price_override_coins >= 0");
                    table.CheckConstraint("ck_chapters_publication_status", "publication_status IN ('Draft', 'Published', 'Hidden')");
                    table.CheckConstraint("ck_chapters_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_chapters_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chapter_pages",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    chapter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_order = table.Column<int>(type: "integer", nullable: false),
                    cloudinary_public_id = table.Column<string>(type: "text", nullable: false),
                    asset_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chapter_pages", x => x.id);
                    table.CheckConstraint("ck_chapter_pages_asset_status_not_blank", "length(btrim(asset_status)) > 0");
                    table.CheckConstraint("ck_chapter_pages_cloudinary_public_id_not_blank", "length(btrim(cloudinary_public_id)) > 0");
                    table.CheckConstraint("ck_chapter_pages_height", "height > 0");
                    table.CheckConstraint("ck_chapter_pages_width", "width > 0");
                    table.ForeignKey(
                        name: "fk_chapter_pages_chapters",
                        column: x => x.chapter_id,
                        principalSchema: "catalog",
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_chapter_pages_chapter_id_page_order",
                schema: "catalog",
                table: "chapter_pages",
                columns: new[] { "chapter_id", "page_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_chapters_comic_id_sort_order",
                schema: "catalog",
                table: "chapters",
                columns: new[] { "comic_id", "sort_order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chapter_pages",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "chapters",
                schema: "catalog");
        }
    }
}
