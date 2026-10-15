using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSePayWebhookIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_transactions",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_transaction_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payment_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    account_number_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    reference_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    matched_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reconciliation_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_transactions", x => x.id);
                    table.CheckConstraint("ck_provider_transactions_amount", "amount_vnd > 0");
                    table.CheckConstraint("ck_provider_transactions_direction", "direction IN ('Incoming', 'Outgoing')");
                    table.CheckConstraint("ck_provider_transactions_payload_fingerprint", "payload_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_provider_transactions_processing", "(processing_status = 'Received' AND reconciliation_reason IS NULL) OR (processing_status = 'ReconciliationRequired' AND reconciliation_reason IS NOT NULL)");
                    table.CheckConstraint("ck_provider_transactions_provider", "provider IN ('SePay')");
                    table.ForeignKey(
                        name: "fk_provider_transactions_payment_orders",
                        column: x => x.matched_order_id,
                        principalSchema: "billing",
                        principalTable: "payment_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    auth_verified = table.Column<bool>(type: "boolean", nullable: false),
                    payload_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delivery_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    processing_result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                    table.CheckConstraint("ck_webhook_deliveries_authenticated_transaction", "auth_verified = TRUE AND provider_transaction_id IS NOT NULL");
                    table.CheckConstraint("ck_webhook_deliveries_payload_reference", "payload_reference ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_webhook_deliveries_processing", "(processing_result IN ('Accepted', 'Duplicate') AND error_code IS NULL) OR (processing_result = 'ReconciliationRequired' AND error_code IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_provider_transactions",
                        column: x => x.provider_transaction_id,
                        principalSchema: "billing",
                        principalTable: "provider_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_transactions_matched_order_id",
                schema: "billing",
                table: "provider_transactions",
                column: "matched_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_provider_transactions_status_verified_id",
                schema: "billing",
                table: "provider_transactions",
                columns: new[] { "processing_status", "verified_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_provider_transactions_provider_external_id",
                schema: "billing",
                table: "provider_transactions",
                columns: new[] { "provider", "provider_transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_provider_transaction_id",
                schema: "billing",
                table: "webhook_deliveries",
                column: "provider_transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_received_id",
                schema: "billing",
                table: "webhook_deliveries",
                columns: new[] { "received_at", "id" },
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "provider_transactions",
                schema: "billing");
        }
    }
}
