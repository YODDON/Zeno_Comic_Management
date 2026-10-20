using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenRefundRequestSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM billing.refund_requests) THEN
                        RAISE EXCEPTION 'RF01_SECURITY_PRECONDITION: legacy unkeyed refund fingerprints require reconciliation before upgrade';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_refund_requests_topup_payment_orders",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropIndex(
                name: "IX_refund_requests_payment_order_id_user_id_payment_order_type",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refund_requests_idempotency",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.AddColumn<string>(
                name: "request_fingerprint_version",
                schema: "billing",
                table: "refund_requests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_payment_orders_refund_snapshot",
                schema: "billing",
                table: "payment_orders",
                columns: new[] { "id", "user_id", "order_type", "amount_vnd" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_paid_coin_lots_refund_snapshot",
                schema: "wallet",
                table: "paid_coin_lots",
                columns: new[] { "topup_order_id", "user_id", "topup_order_type", "granted_coins" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_refund_requests_idempotency",
                schema: "billing",
                table: "refund_requests",
                sql: "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) AND request_fingerprint ~ '^[0-9a-f]{64}$' AND request_fingerprint_version ~ '^[A-Za-z0-9_-]{1,40}$'");

            migrationBuilder.AddForeignKey(
                name: "fk_refund_requests_original_paid_coin_lot_snapshot",
                schema: "billing",
                table: "refund_requests",
                columns: new[] { "payment_order_id", "user_id", "payment_order_type", "required_recovery_coins" },
                principalSchema: "wallet",
                principalTable: "paid_coin_lots",
                principalColumns: new[] { "topup_order_id", "user_id", "topup_order_type", "granted_coins" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_refund_requests_topup_payment_order_snapshot",
                schema: "billing",
                table: "refund_requests",
                columns: new[] { "payment_order_id", "user_id", "payment_order_type", "original_amount_vnd" },
                principalSchema: "billing",
                principalTable: "payment_orders",
                principalColumns: new[] { "id", "user_id", "order_type", "amount_vnd" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_refund_requests_original_paid_coin_lot_snapshot",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refund_requests_idempotency",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_refund_requests_topup_payment_order_snapshot",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_payment_orders_refund_snapshot",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_paid_coin_lots_refund_snapshot",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.DropColumn(
                name: "request_fingerprint_version",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refund_requests_idempotency",
                schema: "billing",
                table: "refund_requests",
                sql: "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) AND request_fingerprint ~ '^[0-9a-f]{64}$'");

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_payment_order_id_user_id_payment_order_type",
                schema: "billing",
                table: "refund_requests",
                columns: new[] { "payment_order_id", "user_id", "payment_order_type" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_refund_requests_topup_payment_orders",
                schema: "billing",
                table: "refund_requests",
                columns: new[] { "payment_order_id", "user_id", "payment_order_type" },
                principalSchema: "billing",
                principalTable: "payment_orders",
                principalColumns: new[] { "id", "user_id", "order_type" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
