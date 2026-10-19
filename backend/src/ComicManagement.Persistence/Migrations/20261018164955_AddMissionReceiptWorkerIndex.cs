using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMissionReceiptWorkerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_action_receipts_pending_worker",
                schema: "missions",
                table: "action_receipts",
                columns: new[] { "processing_status", "received_at", "id" },
                filter: "processing_status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_action_receipts_pending_worker",
                schema: "missions",
                table: "action_receipts");
        }
    }
}
