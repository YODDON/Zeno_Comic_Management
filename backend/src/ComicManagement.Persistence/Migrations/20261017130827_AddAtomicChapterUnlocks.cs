using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAtomicChapterUnlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_chapter_unlock_quotes_id_user_chapter",
                schema: "reading",
                table: "chapter_unlock_quotes",
                columns: new[] { "id", "user_id", "chapter_id" });

            migrationBuilder.CreateTable(
                name: "chapter_unlocks",
                schema: "reading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chapter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_paid_coins = table.Column<long>(type: "bigint", nullable: false),
                    bonus_spent_coins = table.Column<long>(type: "bigint", nullable: false),
                    paid_spent_coins = table.Column<long>(type: "bigint", nullable: false),
                    wallet_paid_available_after = table.Column<long>(type: "bigint", nullable: false),
                    wallet_bonus_available_after = table.Column<long>(type: "bigint", nullable: false),
                    unlocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chapter_unlocks", x => x.id);
                    table.CheckConstraint("ck_chapter_unlocks_spend", "price_paid_coins > 0 AND bonus_spent_coins >= 0 AND paid_spent_coins >= 0 AND price_paid_coins::numeric = bonus_spent_coins::numeric + paid_spent_coins::numeric");
                    table.CheckConstraint("ck_chapter_unlocks_wallet_after", "wallet_paid_available_after >= 0 AND wallet_bonus_available_after >= 0");
                    table.ForeignKey(
                        name: "FK_chapter_unlocks_chapter_unlock_quotes_quote_id_user_id_chap~",
                        columns: x => new { x.quote_id, x.user_id, x.chapter_id },
                        principalSchema: "reading",
                        principalTable: "chapter_unlock_quotes",
                        principalColumns: new[] { "id", "user_id", "chapter_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chapter_unlocks_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalSchema: "catalog",
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chapter_unlocks_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chapter_unlocks_wallet_operations_wallet_operation_id_user_~",
                        columns: x => new { x.wallet_operation_id, x.user_id },
                        principalSchema: "wallet",
                        principalTable: "wallet_operations",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chapter_unlocks_chapter",
                schema: "reading",
                table: "chapter_unlocks",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_chapter_unlocks_quote_id_user_id_chapter_id",
                schema: "reading",
                table: "chapter_unlocks",
                columns: new[] { "quote_id", "user_id", "chapter_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chapter_unlocks_wallet_operation_id_user_id",
                schema: "reading",
                table: "chapter_unlocks",
                columns: new[] { "wallet_operation_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_chapter_unlocks_quote",
                schema: "reading",
                table: "chapter_unlocks",
                column: "quote_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_chapter_unlocks_user_chapter",
                schema: "reading",
                table: "chapter_unlocks",
                columns: new[] { "user_id", "chapter_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_chapter_unlocks_wallet_operation",
                schema: "reading",
                table: "chapter_unlocks",
                column: "wallet_operation_id",
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reading.reject_chapter_unlock_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Chapter unlocks are append-only'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER trg_chapter_unlocks_append_only
                BEFORE UPDATE OR DELETE ON reading.chapter_unlocks
                FOR EACH ROW
                EXECUTE FUNCTION reading.reject_chapter_unlock_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_chapter_unlocks_append_only
                    ON reading.chapter_unlocks;
                DROP FUNCTION IF EXISTS reading.reject_chapter_unlock_mutation();
                """);

            migrationBuilder.DropTable(
                name: "chapter_unlocks",
                schema: "reading");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_chapter_unlock_quotes_id_user_chapter",
                schema: "reading",
                table: "chapter_unlock_quotes");
        }
    }
}
