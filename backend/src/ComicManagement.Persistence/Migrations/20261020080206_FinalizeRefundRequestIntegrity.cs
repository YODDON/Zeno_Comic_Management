using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinalizeRefundRequestIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION billing.enforce_refund_request_snapshot_immutable()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'refund request snapshots cannot be deleted'
                            USING ERRCODE = 'P0001';
                    END IF;

                    IF ROW(
                        OLD.id,
                        OLD.payment_order_id,
                        OLD.user_id,
                        OLD.payment_order_type,
                        OLD.reason,
                        OLD.original_amount_vnd,
                        OLD.fee_amount_vnd,
                        OLD.net_refund_vnd,
                        OLD.required_recovery_coins,
                        OLD.idempotency_key,
                        OLD.request_fingerprint,
                        OLD.request_fingerprint_version,
                        OLD.requested_at)
                    IS DISTINCT FROM ROW(
                        NEW.id,
                        NEW.payment_order_id,
                        NEW.user_id,
                        NEW.payment_order_type,
                        NEW.reason,
                        NEW.original_amount_vnd,
                        NEW.fee_amount_vnd,
                        NEW.net_refund_vnd,
                        NEW.required_recovery_coins,
                        NEW.idempotency_key,
                        NEW.request_fingerprint,
                        NEW.request_fingerprint_version,
                        NEW.requested_at) THEN
                        RAISE EXCEPTION 'refund request snapshot fields are immutable'
                            USING ERRCODE = 'P0001';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_refund_requests_snapshot_immutable
                BEFORE UPDATE OR DELETE ON billing.refund_requests
                FOR EACH ROW
                EXECUTE FUNCTION billing.enforce_refund_request_snapshot_immutable();

                CREATE OR REPLACE FUNCTION billing.enforce_refund_recipient_immutable()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'refund recipients are immutable and cannot be deleted independently'
                        USING ERRCODE = 'P0001';
                END;
                $function$;

                CREATE TRIGGER trg_refund_recipients_immutable
                BEFORE UPDATE OR DELETE ON billing.refund_recipients
                FOR EACH ROW
                EXECUTE FUNCTION billing.enforce_refund_recipient_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_refund_recipients_immutable
                    ON billing.refund_recipients;
                DROP FUNCTION IF EXISTS billing.enforce_refund_recipient_immutable();

                DROP TRIGGER IF EXISTS trg_refund_requests_snapshot_immutable
                    ON billing.refund_requests;
                DROP FUNCTION IF EXISTS billing.enforce_refund_request_snapshot_immutable();
                """);
        }
    }
}
