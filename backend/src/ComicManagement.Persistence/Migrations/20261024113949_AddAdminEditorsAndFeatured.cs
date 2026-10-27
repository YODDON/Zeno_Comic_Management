using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminEditorsAndFeatured : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "featured_comics",
                schema: "catalog",
                columns: table => new
                {
                    comic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_featured_comics", x => x.comic_id);
                    table.CheckConstraint("ck_featured_comics_display_order", "display_order BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_featured_comics_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "featured_configuration",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_featured_configuration", x => x.id);
                    table.CheckConstraint("ck_featured_configuration_singleton", "id = 1");
                    table.CheckConstraint("ck_featured_configuration_version", "version >= 1");
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "featured_configuration",
                columns: new[] { "id", "version", "updated_at" },
                values: new object[] { 1, 1L, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });

            migrationBuilder.CreateIndex(
                name: "ux_featured_comics_display_order",
                schema: "catalog",
                table: "featured_comics",
                column: "display_order",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "featured_comics",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "featured_configuration",
                schema: "catalog");
        }
    }
}
