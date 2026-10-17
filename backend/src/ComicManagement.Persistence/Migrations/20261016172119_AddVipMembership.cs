using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVipMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_orders_type_snapshots",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.EnsureSchema(
                name: "membership");

            migrationBuilder.CreateTable(
                name: "vip_plans",
                schema: "membership",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    price_vnd = table.Column<long>(type: "bigint", nullable: false),
                    duration_hours = table.Column<int>(type: "integer", nullable: false),
                    discount_percent = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vip_plans", x => x.id);
                    table.CheckConstraint("ck_vip_plans_fixed_policy", "duration_hours = 720 AND discount_percent = 20");
                    table.CheckConstraint("ck_vip_plans_price", "price_vnd > 0");
                    table.CheckConstraint("ck_vip_plans_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "vip_subscriptions",
                schema: "membership",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vip_subscriptions", x => x.user_id);
                    table.CheckConstraint("ck_vip_subscriptions_timeline", "expires_at > last_fulfilled_at");
                    table.CheckConstraint("ck_vip_subscriptions_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_vip_subscriptions_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vip_transactions",
                schema: "membership",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_order_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    previous_expiry = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    effective_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    new_expiry = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_hours = table.Column<int>(type: "integer", nullable: false),
                    fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vip_transactions", x => x.id);
                    table.CheckConstraint("ck_vip_transactions_duration", "duration_hours = 720");
                    table.CheckConstraint("ck_vip_transactions_order_type", "payment_order_type = 'VipPurchase'");
                    table.CheckConstraint("ck_vip_transactions_timeline", "effective_start >= fulfilled_at AND new_expiry = effective_start + make_interval(hours => duration_hours) AND created_at = fulfilled_at AND (previous_expiry IS NULL OR effective_start >= previous_expiry)");
                    table.ForeignKey(
                        name: "fk_vip_transactions_payment_order_owner_type",
                        columns: x => new { x.payment_order_id, x.user_id, x.payment_order_type },
                        principalSchema: "billing",
                        principalTable: "payment_orders",
                        principalColumns: new[] { "id", "user_id", "order_type" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vip_transactions_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "membership",
                table: "vip_plans",
                columns: new[] { "id", "code", "discount_percent", "duration_hours", "is_active", "name", "price_vnd", "updated_at", "version" },
                values: new object[] { new Guid("8c884d82-fdc7-4e94-893d-6caa95a22f7a"), "VIP_30_DAY", 20, 720, true, "VIP 30 Days", 29000L, new DateTimeOffset(new DateTime(2026, 10, 17, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 1L });

            migrationBuilder.Sql(
                """
                DO $v01$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM billing.payment_orders
                        WHERE order_type = 'VipPurchase'
                          AND (
                              vip_plan_id <> '8c884d82-fdc7-4e94-893d-6caa95a22f7a'::uuid
                              OR vip_duration_hours_snapshot <> 720
                              OR vip_discount_percent_snapshot <> 20
                          )
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            MESSAGE = 'V01 migration blocked: legacy VipPurchase snapshots require reconciliation';
                    END IF;
                END
                $v01$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_payment_orders_vip_plan_id",
                schema: "billing",
                table: "payment_orders",
                column: "vip_plan_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_orders_type_snapshots",
                schema: "billing",
                table: "payment_orders",
                sql: "(order_type = 'TopUp' AND amount_vnd BETWEEN 10000 AND 2000000 AND amount_vnd % 1000 = 0 AND paid_coins_snapshot = amount_vnd / 10 AND vip_plan_id IS NULL AND vip_duration_hours_snapshot IS NULL AND vip_discount_percent_snapshot IS NULL) OR (order_type = 'VipPurchase' AND paid_coins_snapshot IS NULL AND vip_plan_id IS NOT NULL AND vip_duration_hours_snapshot = 720 AND vip_discount_percent_snapshot = 20)");

            migrationBuilder.CreateIndex(
                name: "ux_vip_plans_code",
                schema: "membership",
                table: "vip_plans",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_vip_plans_single_active",
                schema: "membership",
                table: "vip_plans",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_vip_subscriptions_expires_at",
                schema: "membership",
                table: "vip_subscriptions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_vip_transactions_payment_order_id_user_id_payment_order_type",
                schema: "membership",
                table: "vip_transactions",
                columns: new[] { "payment_order_id", "user_id", "payment_order_type" });

            migrationBuilder.CreateIndex(
                name: "ix_vip_transactions_user_fulfilled_id",
                schema: "membership",
                table: "vip_transactions",
                columns: new[] { "user_id", "fulfilled_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_vip_transactions_payment_order_id",
                schema: "membership",
                table: "vip_transactions",
                column: "payment_order_id",
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION membership.reject_vip_transaction_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'VIP transactions are append-only'
                        USING ERRCODE = '23000';
                END;
                $function$;

                CREATE TRIGGER vip_transactions_append_only
                BEFORE UPDATE OR DELETE ON membership.vip_transactions
                FOR EACH ROW
                EXECUTE FUNCTION membership.reject_vip_transaction_mutation();
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_orders_vip_plans",
                schema: "billing",
                table: "payment_orders",
                column: "vip_plan_id",
                principalSchema: "membership",
                principalTable: "vip_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS vip_transactions_append_only
                    ON membership.vip_transactions;
                DROP FUNCTION IF EXISTS membership.reject_vip_transaction_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_payment_orders_vip_plans",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.DropTable(
                name: "vip_plans",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "vip_subscriptions",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "vip_transactions",
                schema: "membership");

            migrationBuilder.DropIndex(
                name: "IX_payment_orders_vip_plan_id",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_orders_type_snapshots",
                schema: "billing",
                table: "payment_orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_orders_type_snapshots",
                schema: "billing",
                table: "payment_orders",
                sql: "(order_type = 'TopUp' AND amount_vnd BETWEEN 10000 AND 2000000 AND amount_vnd % 1000 = 0 AND paid_coins_snapshot = amount_vnd / 10 AND vip_plan_id IS NULL AND vip_duration_hours_snapshot IS NULL AND vip_discount_percent_snapshot IS NULL) OR (order_type = 'VipPurchase' AND paid_coins_snapshot IS NULL AND vip_plan_id IS NOT NULL AND vip_duration_hours_snapshot > 0 AND vip_discount_percent_snapshot BETWEEN 0 AND 100)");
        }
    }
}
