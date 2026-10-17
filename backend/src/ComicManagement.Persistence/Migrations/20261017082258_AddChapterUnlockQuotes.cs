using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterUnlockQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chapter_unlock_quotes",
                schema: "reading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chapter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_price_coins = table.Column<long>(type: "bigint", nullable: false),
                    vip_discount_applied = table.Column<bool>(type: "boolean", nullable: false),
                    vip_discount_percent = table.Column<int>(type: "integer", nullable: false),
                    final_price_coins = table.Column<long>(type: "bigint", nullable: false),
                    bonus_to_spend = table.Column<long>(type: "bigint", nullable: false),
                    paid_to_spend = table.Column<long>(type: "bigint", nullable: false),
                    price_revision = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chapter_unlock_quotes", x => x.id);
                    table.CheckConstraint("ck_chapter_unlock_quotes_estimate", "bonus_to_spend >= 0 AND paid_to_spend >= 0 AND bonus_to_spend::numeric + paid_to_spend::numeric <= final_price_coins::numeric");
                    table.CheckConstraint("ck_chapter_unlock_quotes_expiry", "expires_at = created_at + interval '5 minutes'");
                    table.CheckConstraint("ck_chapter_unlock_quotes_price", "base_price_coins > 0 AND final_price_coins > 0 AND ((vip_discount_applied AND vip_discount_percent = 20 AND final_price_coins = CEIL(base_price_coins::numeric * 80 / 100)::bigint) OR (NOT vip_discount_applied AND vip_discount_percent = 0 AND final_price_coins = base_price_coins))");
                    table.CheckConstraint("ck_chapter_unlock_quotes_revision", "price_revision ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_chapter_unlock_quotes_chapters",
                        column: x => x.chapter_id,
                        principalSchema: "catalog",
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chapter_unlock_quotes_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chapter_unlock_quotes_chapter_expiry",
                schema: "reading",
                table: "chapter_unlock_quotes",
                columns: new[] { "chapter_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chapter_unlock_quotes_user_expiry",
                schema: "reading",
                table: "chapter_unlock_quotes",
                columns: new[] { "user_id", "expires_at" });

            migrationBuilder.Sql(
                """
                CREATE FUNCTION reading.enforce_chapter_unlock_quote_lifecycle()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        RAISE EXCEPTION 'Chapter unlock quotes are immutable'
                            USING ERRCODE = '23000';
                    END IF;

                    IF OLD.expires_at + interval '30 days' > clock_timestamp() THEN
                        RAISE EXCEPTION 'Chapter unlock quote retention window has not elapsed'
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN OLD;
                END;
                $function$;

                CREATE TRIGGER chapter_unlock_quotes_lifecycle
                BEFORE UPDATE OR DELETE ON reading.chapter_unlock_quotes
                FOR EACH ROW
                EXECUTE FUNCTION reading.enforce_chapter_unlock_quote_lifecycle();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS chapter_unlock_quotes_lifecycle
                    ON reading.chapter_unlock_quotes;
                DROP FUNCTION IF EXISTS reading.enforce_chapter_unlock_quote_lifecycle();
                """);

            migrationBuilder.DropTable(
                name: "chapter_unlock_quotes",
                schema: "reading");
        }
    }
}
