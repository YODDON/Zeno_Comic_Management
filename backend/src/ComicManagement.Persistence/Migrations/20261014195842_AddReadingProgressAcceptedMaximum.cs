using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReadingProgressAcceptedMaximum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "accepted_max_progress_basis_points",
                schema: "reading",
                table: "reading_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_reading_progress_accepted_progress",
                schema: "reading",
                table: "reading_progress",
                sql: "accepted_max_progress_basis_points BETWEEN 0 AND 10000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reading_progress_accepted_progress",
                schema: "reading",
                table: "reading_progress");

            migrationBuilder.DropColumn(
                name: "accepted_max_progress_basis_points",
                schema: "reading",
                table: "reading_progress");
        }
    }
}
