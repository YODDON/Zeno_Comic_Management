using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "payment_orders",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    paid_coins_snapshot = table.Column<long>(type: "bigint", nullable: true),
                    vip_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vip_duration_hours_snapshot = table.Column<int>(type: "integer", nullable: true),
                    vip_discount_percent_snapshot = table.Column<int>(type: "integer", nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_payment_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    fulfillment_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verified_paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_orders", x => x.id);
                    table.CheckConstraint("ck_payment_orders_amount", "amount_vnd > 0");
                    table.CheckConstraint("ck_payment_orders_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_payment_orders_fulfillment_status", "fulfillment_status IN ('NotStarted', 'Retryable', 'Fulfilled', 'ManualReview')");
                    table.CheckConstraint("ck_payment_orders_idempotency", "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) AND request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_payment_orders_provider", "provider IN ('SePay')");
                    table.CheckConstraint("ck_payment_orders_provider_status", "provider_payment_status IN ('Pending', 'Verified', 'Rejected', 'ReconciliationRequired')");
                    table.CheckConstraint("ck_payment_orders_type_snapshots", "(order_type = 'TopUp' AND amount_vnd BETWEEN 10000 AND 2000000 AND amount_vnd % 1000 = 0 AND paid_coins_snapshot = amount_vnd / 10 AND vip_plan_id IS NULL AND vip_duration_hours_snapshot IS NULL AND vip_discount_percent_snapshot IS NULL) OR (order_type = 'VipPurchase' AND paid_coins_snapshot IS NULL AND vip_plan_id IS NOT NULL AND vip_duration_hours_snapshot > 0 AND vip_discount_percent_snapshot BETWEEN 0 AND 100)");
                    table.CheckConstraint("ck_payment_orders_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_payment_orders_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_orders_pending_expiry",
                schema: "billing",
                table: "payment_orders",
                columns: new[] { "provider_payment_status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_orders_user_created_id",
                schema: "billing",
                table: "payment_orders",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_payment_orders_order_code",
                schema: "billing",
                table: "payment_orders",
                column: "order_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_payment_orders_user_operation_idempotency",
                schema: "billing",
                table: "payment_orders",
                columns: new[] { "user_id", "order_type", "idempotency_key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_paid_coin_lots_topup_payment_orders",
                schema: "wallet",
                table: "paid_coin_lots",
                column: "topup_order_id",
                principalSchema: "billing",
                principalTable: "payment_orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_paid_coin_lots_topup_payment_orders",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.DropTable(
                name: "payment_orders",
                schema: "billing");
        }
    }
}
