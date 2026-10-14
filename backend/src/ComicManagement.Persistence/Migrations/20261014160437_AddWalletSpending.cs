using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletSpending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wallet_allocations",
                schema: "wallet",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paid_coin_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_coins = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_allocations", x => x.id);
                    table.CheckConstraint("ck_wallet_allocations_amount", "amount_coins > 0");
                    table.ForeignKey(
                        name: "fk_wallet_allocations_operations",
                        column: x => x.operation_id,
                        principalSchema: "wallet",
                        principalTable: "wallet_operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wallet_allocations_paid_coin_lots",
                        column: x => x.paid_coin_lot_id,
                        principalSchema: "wallet",
                        principalTable: "paid_coin_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_allocations_paid_coin_lot_id",
                schema: "wallet",
                table: "wallet_allocations",
                column: "paid_coin_lot_id");

            migrationBuilder.CreateIndex(
                name: "ux_wallet_allocations_operation_lot",
                schema: "wallet",
                table: "wallet_allocations",
                columns: new[] { "operation_id", "paid_coin_lot_id" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION wallet.enforce_wallet_allocation_ownership()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    operation_user_id uuid;
                    lot_user_id uuid;
                BEGIN
                    SELECT user_id INTO operation_user_id
                    FROM wallet.wallet_operations
                    WHERE id = NEW.operation_id;

                    SELECT user_id INTO lot_user_id
                    FROM wallet.paid_coin_lots
                    WHERE id = NEW.paid_coin_lot_id;

                    IF operation_user_id IS NULL OR
                       lot_user_id IS NULL OR
                       operation_user_id <> lot_user_id THEN
                        RAISE EXCEPTION 'Wallet allocation ownership mismatch.'
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER wallet_allocations_enforce_ownership
                BEFORE INSERT ON wallet.wallet_allocations
                FOR EACH ROW EXECUTE FUNCTION wallet.enforce_wallet_allocation_ownership();

                CREATE TRIGGER wallet_allocations_append_only
                BEFORE UPDATE OR DELETE ON wallet.wallet_allocations
                FOR EACH ROW EXECUTE FUNCTION wallet.reject_wallet_ledger_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS wallet_allocations_enforce_ownership
                    ON wallet.wallet_allocations;
                DROP FUNCTION IF EXISTS wallet.enforce_wallet_allocation_ownership();
                """);

            migrationBuilder.DropTable(
                name: "wallet_allocations",
                schema: "wallet");
        }
    }
}
