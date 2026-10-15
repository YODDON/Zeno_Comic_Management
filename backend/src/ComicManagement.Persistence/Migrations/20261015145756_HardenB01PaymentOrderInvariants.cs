using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenB01PaymentOrderInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_paid_coin_lots_topup_payment_orders",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expired_at",
                schema: "billing",
                table: "payment_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "order_status",
                schema: "billing",
                table: "payment_orders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.Sql(
                "ALTER TABLE billing.payment_orders ALTER COLUMN order_status DROP DEFAULT;");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_payment_orders_id_user_id",
                schema: "billing",
                table: "payment_orders",
                columns: new[] { "id", "user_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_orders_order_status",
                schema: "billing",
                table: "payment_orders",
                sql: "(order_status = 'Pending' AND expired_at IS NULL) OR (order_status = 'Expired' AND expired_at IS NOT NULL AND expired_at >= expires_at)");

            migrationBuilder.CreateIndex(
                name: "ux_paid_coin_lots_topup_order_user",
                schema: "wallet",
                table: "paid_coin_lots",
                columns: new[] { "topup_order_id", "user_id" },
                unique: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE wallet.paid_coin_lots
                ADD CONSTRAINT fk_paid_coin_lots_topup_payment_orders
                FOREIGN KEY (topup_order_id, user_id)
                REFERENCES billing.payment_orders (id, user_id)
                ON DELETE RESTRICT
                NOT VALID;

                DO $migration$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM wallet.paid_coin_lots AS lot
                        LEFT JOIN billing.payment_orders AS payment_order
                          ON payment_order.id = lot.topup_order_id
                         AND payment_order.user_id = lot.user_id
                        WHERE payment_order.id IS NULL)
                    THEN
                        ALTER TABLE wallet.paid_coin_lots
                        VALIDATE CONSTRAINT fk_paid_coin_lots_topup_payment_orders;
                    END IF;
                END
                $migration$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_paid_coin_lots_topup_payment_orders",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_payment_orders_id_user_id",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_orders_order_status",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.DropIndex(
                name: "ux_paid_coin_lots_topup_order_user",
                schema: "wallet",
                table: "paid_coin_lots");

            migrationBuilder.DropColumn(
                name: "expired_at",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.DropColumn(
                name: "order_status",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.Sql(
                """
                ALTER TABLE wallet.paid_coin_lots
                ADD CONSTRAINT fk_paid_coin_lots_topup_payment_orders
                FOREIGN KEY (topup_order_id)
                REFERENCES billing.payment_orders (id)
                ON DELETE RESTRICT
                NOT VALID;
                """);
        }
    }
}
