using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refund_requests",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_order_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    original_amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    fee_amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    net_refund_vnd = table.Column<long>(type: "bigint", nullable: false),
                    required_recovery_coins = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_requests", x => x.id);
                    table.UniqueConstraint("ak_refund_requests_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_refund_requests_amounts", "original_amount_vnd > 0 AND fee_amount_vnd = original_amount_vnd * 5 / 100 AND net_refund_vnd = original_amount_vnd - fee_amount_vnd");
                    table.CheckConstraint("ck_refund_requests_idempotency", "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) AND request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_refund_requests_order_type", "payment_order_type = 'TopUp'");
                    table.CheckConstraint("ck_refund_requests_recovery", "required_recovery_coins > 0");
                    table.CheckConstraint("ck_refund_requests_status", "status IN ('Requested', 'UnderReview', 'RecoveryReserved', 'TransferPending', 'Completed', 'Rejected', 'NeedsReconciliation', 'TransferConfirmedFailed', 'ReservationReleased')");
                    table.CheckConstraint("ck_refund_requests_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_refund_requests_topup_payment_orders",
                        columns: x => new { x.payment_order_id, x.user_id, x.payment_order_type },
                        principalSchema: "billing",
                        principalTable: "payment_orders",
                        principalColumns: new[] { "id", "user_id", "order_type" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_requests_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_recipients",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    account_number_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    account_holder_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    account_number_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    account_number_masked = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    account_holder_masked = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    encryption_key_version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_recipients", x => x.id);
                    table.CheckConstraint("ck_refund_recipients_ciphertext", "octet_length(account_number_encrypted) > 0 AND octet_length(account_holder_encrypted) > 0");
                    table.CheckConstraint("ck_refund_recipients_last4", "account_number_last4 ~ '^[0-9]{4}$'");
                    table.ForeignKey(
                        name: "fk_refund_recipients_refund_requests",
                        column: x => x.refund_request_id,
                        principalSchema: "billing",
                        principalTable: "refund_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_refund_recipients_refund_request_id",
                schema: "billing",
                table: "refund_recipients",
                column: "refund_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_payment_order_id_user_id_payment_order_type",
                schema: "billing",
                table: "refund_requests",
                columns: new[] { "payment_order_id", "user_id", "payment_order_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_requests_user_requested_id",
                schema: "billing",
                table: "refund_requests",
                columns: new[] { "user_id", "requested_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_refund_requests_payment_order_id",
                schema: "billing",
                table: "refund_requests",
                column: "payment_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_refund_requests_user_idempotency",
                schema: "billing",
                table: "refund_requests",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refund_recipients",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "refund_requests",
                schema: "billing");
        }
    }
}
