using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComicRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "interaction");

            migrationBuilder.CreateTable(
                name: "comic_ratings",
                schema: "interaction",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stars = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comic_ratings", x => new { x.user_id, x.comic_id });
                    table.CheckConstraint("ck_comic_ratings_stars", "stars BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_comic_ratings_timestamps", "updated_at >= created_at");
                    table.ForeignKey(
                        name: "fk_comic_ratings_comics",
                        column: x => x.comic_id,
                        principalSchema: "catalog",
                        principalTable: "comics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comic_ratings_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comic_ratings_comic_id",
                schema: "interaction",
                table: "comic_ratings",
                column: "comic_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comic_ratings",
                schema: "interaction");
        }
    }
}
