using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTopUpFulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_paid_coin_lots_topup_payment_orders",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_transactions_processing",
                schema: "billing",
                table: "provider_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_orders_order_status",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.AddColumn<string>(
                name: "topup_order_type",
                schema: "wallet",
                table: "paid_coin_lots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM wallet.paid_coin_lots AS lot
                        LEFT JOIN billing.payment_orders AS payment_order
                          ON payment_order.id = lot.topup_order_id
                         AND payment_order.user_id = lot.user_id
                        WHERE payment_order.id IS NULL
                           OR payment_order.order_type <> 'TopUp')
                    THEN
                        RAISE EXCEPTION
                            'B03 cannot validate PaidCoinLot ownership: reconcile legacy lots before migration.';
                    END IF;
                END
                $migration$;

                UPDATE wallet.paid_coin_lots
                SET topup_order_type = 'TopUp';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "topup_order_type",
                schema: "wallet",
                table: "paid_coin_lots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_payment_orders_id_user_id_order_type",
                schema: "billing",
                table: "payment_orders",
                columns: new[] { "id", "user_id", "order_type" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_transactions_processing",
                schema: "billing",
                table: "provider_transactions",
                sql: "(processing_status = 'Received' AND reconciliation_reason IS NULL AND matched_order_id IS NULL) OR (processing_status = 'ReconciliationRequired' AND reconciliation_reason IS NOT NULL) OR (processing_status = 'Fulfilled' AND reconciliation_reason IS NULL AND matched_order_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_orders_order_status",
                schema: "billing",
                table: "payment_orders",
                sql: "(order_status = 'Pending' AND expired_at IS NULL AND provider_payment_status = 'Pending' AND fulfillment_status = 'NotStarted' AND verified_paid_at IS NULL AND fulfilled_at IS NULL) OR (order_status = 'Expired' AND expired_at IS NOT NULL AND expired_at >= expires_at AND provider_payment_status = 'Pending' AND fulfillment_status = 'NotStarted' AND verified_paid_at IS NULL AND fulfilled_at IS NULL) OR (order_status = 'Succeeded' AND expired_at IS NULL AND provider_payment_status = 'Verified' AND fulfillment_status = 'Fulfilled' AND verified_paid_at IS NOT NULL AND fulfilled_at = verified_paid_at AND fulfilled_at < expires_at)");

            migrationBuilder.CreateIndex(
                name: "ux_paid_coin_lots_topup_order_owner_type",
                schema: "wallet",
                table: "paid_coin_lots",
                columns: new[] { "topup_order_id", "user_id", "topup_order_type" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_paid_coin_lots_topup_order_type",
                schema: "wallet",
                table: "paid_coin_lots",
                sql: "topup_order_type = 'TopUp'");

            migrationBuilder.AddForeignKey(
                name: "fk_paid_coin_lots_topup_payment_orders",
                schema: "wallet",
                table: "paid_coin_lots",
                columns: new[] { "topup_order_id", "user_id", "topup_order_type" },
                principalSchema: "billing",
                principalTable: "payment_orders",
                principalColumns: new[] { "id", "user_id", "order_type" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_paid_coin_lots_topup_payment_orders",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_transactions_processing",
                schema: "billing",
                table: "provider_transactions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_payment_orders_id_user_id_order_type",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_orders_order_status",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.DropIndex(
                name: "ux_paid_coin_lots_topup_order_owner_type",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.DropCheckConstraint(
                name: "ck_paid_coin_lots_topup_order_type",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.DropColumn(
                name: "topup_order_type",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_transactions_processing",
                schema: "billing",
                table: "provider_transactions",
                sql: "(processing_status = 'Received' AND reconciliation_reason IS NULL) OR (processing_status = 'ReconciliationRequired' AND reconciliation_reason IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_orders_order_status",
                schema: "billing",
                table: "payment_orders",
                sql: "(order_status = 'Pending' AND expired_at IS NULL) OR (order_status = 'Expired' AND expired_at IS NOT NULL AND expired_at >= expires_at)");

            migrationBuilder.AddForeignKey(
                name: "fk_paid_coin_lots_topup_payment_orders",
                schema: "wallet",
                table: "paid_coin_lots",
                columns: new[] { "topup_order_id", "user_id" },
                principalSchema: "billing",
                principalTable: "payment_orders",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
