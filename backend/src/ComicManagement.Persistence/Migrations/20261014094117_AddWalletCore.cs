using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wallet");

            migrationBuilder.CreateTable(
                name: "paid_coin_lots",
                schema: "wallet",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topup_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_coins = table.Column<long>(type: "bigint", nullable: false),
                    spent_coins = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    refunded_coins = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    reserved_coins = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paid_coin_lots", x => x.id);
                    table.CheckConstraint("ck_paid_coin_lots_capacity", "granted_coins::numeric >= spent_coins::numeric + refunded_coins::numeric + reserved_coins::numeric");
                    table.CheckConstraint("ck_paid_coin_lots_granted", "granted_coins > 0");
                    table.CheckConstraint("ck_paid_coin_lots_refunded", "refunded_coins >= 0");
                    table.CheckConstraint("ck_paid_coin_lots_reserved", "reserved_coins >= 0");
                    table.CheckConstraint("ck_paid_coin_lots_spent", "spent_coins >= 0");
                    table.CheckConstraint("ck_paid_coin_lots_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_paid_coin_lots_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_operations",
                schema: "wallet",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_operations", x => x.id);
                    table.UniqueConstraint("ak_wallet_operations_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_wallet_operations_idempotency_key", "idempotency_key IS NULL OR length(btrim(idempotency_key)) > 0");
                    table.CheckConstraint("ck_wallet_operations_operation_type", "length(btrim(operation_type)) > 0");
                    table.CheckConstraint("ck_wallet_operations_source_type", "length(btrim(source_type)) > 0");
                    table.ForeignKey(
                        name: "fk_wallet_operations_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                schema: "wallet",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paid_available = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    paid_reserved = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    bonus_available = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallets", x => x.user_id);
                    table.CheckConstraint("ck_wallets_bonus_available", "bonus_available >= 0");
                    table.CheckConstraint("ck_wallets_paid_available", "paid_available >= 0");
                    table.CheckConstraint("ck_wallets_paid_reserved", "paid_reserved >= 0");
                    table.CheckConstraint("ck_wallets_timestamps", "updated_at >= created_at");
                    table.CheckConstraint("ck_wallets_total_available", "paid_available::numeric + bonus_available::numeric <= 9223372036854775807");
                    table.CheckConstraint("ck_wallets_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_wallets_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_entries",
                schema: "wallet",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coin_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    bucket = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    delta_coins = table.Column<long>(type: "bigint", nullable: false),
                    leg_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_entries", x => x.id);
                    table.CheckConstraint("ck_wallet_entries_bonus_bucket", "coin_type <> 'Bonus' OR bucket = 'Available'");
                    table.CheckConstraint("ck_wallet_entries_bucket", "bucket IN ('Available', 'Reserved')");
                    table.CheckConstraint("ck_wallet_entries_coin_type", "coin_type IN ('Paid', 'Bonus')");
                    table.CheckConstraint("ck_wallet_entries_delta", "delta_coins <> 0");
                    table.CheckConstraint("ck_wallet_entries_leg_order", "leg_order >= 1");
                    table.ForeignKey(
                        name: "fk_wallet_entries_operation_owner",
                        columns: x => new { x.operation_id, x.user_id },
                        principalSchema: "wallet",
                        principalTable: "wallet_operations",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wallet_entries_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_paid_coin_lots_user_created_id",
                schema: "wallet",
                table: "paid_coin_lots",
                columns: new[] { "user_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_paid_coin_lots_topup_order_id",
                schema: "wallet",
                table: "paid_coin_lots",
                column: "topup_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_operation_id_user_id",
                schema: "wallet",
                table: "wallet_entries",
                columns: new[] { "operation_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_entries_user_created_id",
                schema: "wallet",
                table: "wallet_entries",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_wallet_entries_operation_leg",
                schema: "wallet",
                table: "wallet_entries",
                columns: new[] { "operation_id", "leg_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_wallet_operations_business_source",
                schema: "wallet",
                table: "wallet_operations",
                columns: new[] { "operation_type", "source_type", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_wallet_operations_user_type_idempotency",
                schema: "wallet",
                table: "wallet_operations",
                columns: new[] { "user_id", "operation_type", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION wallet.reject_wallet_ledger_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'wallet ledger rows are append-only'
                        USING ERRCODE = '55000';
                END;
                $function$;

                CREATE TRIGGER wallet_operations_append_only
                BEFORE UPDATE OR DELETE ON wallet.wallet_operations
                FOR EACH ROW EXECUTE FUNCTION wallet.reject_wallet_ledger_mutation();

                CREATE TRIGGER wallet_entries_append_only
                BEFORE UPDATE OR DELETE ON wallet.wallet_entries
                FOR EACH ROW EXECUTE FUNCTION wallet.reject_wallet_ledger_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paid_coin_lots",
                schema: "wallet");

            migrationBuilder.DropTable(
                name: "wallet_entries",
                schema: "wallet");

            migrationBuilder.DropTable(
                name: "wallets",
                schema: "wallet");

            migrationBuilder.DropTable(
                name: "wallet_operations",
                schema: "wallet");

            migrationBuilder.Sql(
                "DROP FUNCTION wallet.reject_wallet_ledger_mutation();");
        }
    }
}
