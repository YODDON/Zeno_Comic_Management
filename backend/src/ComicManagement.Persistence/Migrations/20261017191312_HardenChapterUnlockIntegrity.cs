using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenChapterUnlockIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS reading."IX_chapter_unlocks_quote_id_user_id_chapter_id";
                DROP INDEX IF EXISTS reading."IX_chapter_unlocks_wallet_operation_id_user_id";

                CREATE OR REPLACE FUNCTION reading.chapter_unlock_financial_integrity_valid(
                    unlock_id uuid)
                RETURNS boolean
                LANGUAGE sql
                STABLE
                AS $$
                    SELECT EXISTS (
                        SELECT 1
                        FROM reading.chapter_unlocks AS unlock
                        JOIN wallet.wallet_operations AS operation
                          ON operation.id = unlock.wallet_operation_id
                         AND operation.user_id = unlock.user_id
                        WHERE unlock.id = unlock_id
                          AND operation.operation_type = 'ChapterUnlock'
                          AND operation.source_type = 'ReadingUnlock'
                          AND operation.source_id = unlock.id
                          AND NOT EXISTS (
                              SELECT 1
                              FROM wallet.wallet_entries AS invalid_entry
                              WHERE invalid_entry.operation_id = operation.id
                                AND (
                                    invalid_entry.user_id <> unlock.user_id OR
                                    invalid_entry.bucket <> 'Available' OR
                                    invalid_entry.delta_coins >= 0 OR
                                    invalid_entry.coin_type NOT IN ('Bonus', 'Paid')))
                          AND COALESCE((
                              SELECT SUM(-(entry.delta_coins::numeric))
                              FROM wallet.wallet_entries AS entry
                              WHERE entry.operation_id = operation.id
                                AND entry.coin_type = 'Bonus'), 0) = unlock.bonus_spent_coins
                          AND COALESCE((
                              SELECT SUM(-(entry.delta_coins::numeric))
                              FROM wallet.wallet_entries AS entry
                              WHERE entry.operation_id = operation.id
                                AND entry.coin_type = 'Paid'), 0) = unlock.paid_spent_coins
                          AND COALESCE((
                              SELECT SUM(allocation.amount_coins::numeric)
                              FROM wallet.wallet_allocations AS allocation
                              WHERE allocation.operation_id = operation.id), 0) = unlock.paid_spent_coins
                    );
                $$;

                DO $$
                DECLARE
                    invalid_unlock_id uuid;
                BEGIN
                    SELECT id
                    INTO invalid_unlock_id
                    FROM reading.chapter_unlocks
                    WHERE NOT reading.chapter_unlock_financial_integrity_valid(id)
                    LIMIT 1;

                    IF invalid_unlock_id IS NOT NULL THEN
                        RAISE EXCEPTION
                            'Existing chapter unlock % has invalid financial artifacts',
                            invalid_unlock_id
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $$;

                CREATE OR REPLACE FUNCTION reading.enforce_chapter_unlock_financial_integrity()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NOT reading.chapter_unlock_financial_integrity_valid(NEW.id) THEN
                        RAISE EXCEPTION
                            'Chapter unlock financial artifacts do not match the entitlement'
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER trg_chapter_unlock_financial_integrity
                AFTER INSERT ON reading.chapter_unlocks
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION reading.enforce_chapter_unlock_financial_integrity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_chapter_unlock_financial_integrity
                    ON reading.chapter_unlocks;
                DROP FUNCTION IF EXISTS reading.enforce_chapter_unlock_financial_integrity();
                DROP FUNCTION IF EXISTS reading.chapter_unlock_financial_integrity_valid(uuid);

                CREATE INDEX IF NOT EXISTS "IX_chapter_unlocks_quote_id_user_id_chapter_id"
                    ON reading.chapter_unlocks (quote_id, user_id, chapter_id);
                CREATE INDEX IF NOT EXISTS "IX_chapter_unlocks_wallet_operation_id_user_id"
                    ON reading.chapter_unlocks (wallet_operation_id, user_id);
                """);
        }
    }
}
