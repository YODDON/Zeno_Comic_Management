using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogTaxonomyAndComics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "authors",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authors", x => x.id);
                    table.CheckConstraint("ck_authors_name_not_blank", "length(btrim(name)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "comics",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    series_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    publication_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    first_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cover_asset_id = table.Column<string>(type: "text", nullable: true),
                    default_price_coins = table.Column<long>(type: "bigint", nullable: false),
                    default_vip_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comics", x => x.id);
                    table.CheckConstraint("ck_comics_default_price_coins", "default_price_coins >= 0");
                    table.CheckConstraint("ck_comics_publication_status", "publication_status IN ('Draft', 'Published', 'Hidden')");
                    table.CheckConstraint("ck_comics_series_status", "series_status IN ('Ongoing', 'Completed', 'Hiatus')");
                    table.CheckConstraint("ck_comics_slug_not_blank", "length(btrim(slug)) > 0");
                    table.CheckConstraint("ck_comics_title_not_blank", "length(btrim(title)) > 0");
                    table.CheckConstraint("ck_comics_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "genres",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_genres", x => x.id);
                    table.CheckConstraint("ck_genres_name_not_blank", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_genres_slug_not_blank", "length(btrim(slug)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "translation_groups",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_translation_groups", x => x.id);
                    table.CheckConstraint("ck_translation_groups_name_not_blank", "length(btrim(name)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "comic_authors",
                schema: "catalog",
                columns: table => new
                {
                    comic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comic_authors", x => new { x.comic_id, x.author_id });
                    table.ForeignKey(
                        name: "fk_comic_authors_authors",
                        column: x => x.author_id,
                        principalSchema: "catalog",
                        principalTable: "authors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_comic_authors_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comic_genres",
                schema: "catalog",
                columns: table => new
                {
                    comic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    genre_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comic_genres", x => new { x.comic_id, x.genre_id });
                    table.ForeignKey(
                        name: "fk_comic_genres_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_comic_genres_genres",
                        column: x => x.genre_id,
                        principalSchema: "catalog",
                        principalTable: "genres",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comic_translation_groups",
                schema: "catalog",
                columns: table => new
                {
                    comic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    translation_group_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comic_translation_groups", x => new { x.comic_id, x.translation_group_id });
                    table.ForeignKey(
                        name: "fk_comic_translation_groups_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_comic_translation_groups_translation_groups",
                        column: x => x.translation_group_id,
                        principalSchema: "catalog",
                        principalTable: "translation_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comic_authors_author_id",
                schema: "catalog",
                table: "comic_authors",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_comic_genres_genre_id",
                schema: "catalog",
                table: "comic_genres",
                column: "genre_id");

            migrationBuilder.CreateIndex(
                name: "ix_comic_translation_groups_translation_group_id",
                schema: "catalog",
                table: "comic_translation_groups",
                column: "translation_group_id");

            migrationBuilder.CreateIndex(
                name: "ux_comics_slug",
                schema: "catalog",
                table: "comics",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_genres_slug",
                schema: "catalog",
                table: "genres",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comic_authors",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "comic_genres",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "comic_translation_groups",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "authors",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "genres",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "comics",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "translation_groups",
                schema: "catalog");
        }
    }
}
