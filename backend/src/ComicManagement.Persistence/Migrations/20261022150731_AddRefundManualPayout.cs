using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundManualPayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $precondition$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM billing.refund_requests
                        WHERE status IN ('TransferPending','Completed','NeedsReconciliation',
                                         'TransferConfirmedFailed','ReservationReleased')) THEN
                        RAISE EXCEPTION 'RF03_PRECONDITION_EXISTING_PAYOUT_STATE_REQUIRES_RECONCILIATION'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $precondition$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_refund_requests_review_lifecycle",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.CreateTable(
                name: "refund_transfer_records",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    evidence_reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmation_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    verification_idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    verification_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    resolution_operation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_transfer_records", x => x.id);
                    table.CheckConstraint("ck_refund_transfer_records_amount", "amount_vnd > 0");
                    table.CheckConstraint("ck_refund_transfer_records_evidence", "length(btrim(bank_reference)) > 0 AND bank_reference = btrim(bank_reference) AND length(btrim(evidence_reference)) > 0 AND evidence_reference = btrim(evidence_reference) AND length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) AND request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_refund_transfer_records_status", "status IN ('ReportedSucceeded','ReportedFailed','NeedsReconciliation','VerifiedSucceeded','VerifiedFailed')");
                    table.CheckConstraint("ck_refund_transfer_records_verification", "(status IN ('ReportedSucceeded','ReportedFailed','NeedsReconciliation') AND verified_by IS NULL AND verified_at IS NULL AND confirmation_note IS NULL AND verification_idempotency_key IS NULL AND verification_fingerprint IS NULL AND resolution_operation_id IS NULL) OR (status IN ('VerifiedSucceeded','VerifiedFailed') AND verified_by IS NOT NULL AND verified_at IS NOT NULL AND verified_at >= recorded_at AND length(btrim(confirmation_note)) > 0 AND confirmation_note = btrim(confirmation_note) AND length(btrim(verification_idempotency_key)) > 0 AND verification_idempotency_key = btrim(verification_idempotency_key) AND verification_fingerprint ~ '^[0-9a-f]{64}$' AND resolution_operation_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_refund_transfer_records_recorded_by",
                        column: x => x.recorded_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_transfer_records_refund_owner",
                        columns: x => new { x.refund_request_id, x.user_id },
                        principalSchema: "billing",
                        principalTable: "refund_requests",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_transfer_records_resolution_operation_owner",
                        columns: x => new { x.resolution_operation_id, x.user_id },
                        principalSchema: "wallet",
                        principalTable: "wallet_operations",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_transfer_records_verified_by",
                        column: x => x.verified_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_refund_requests_review_lifecycle",
                schema: "billing",
                table: "refund_requests",
                sql: "(status = 'Requested' AND reviewed_by IS NULL AND reviewed_at IS NULL AND approved_by IS NULL AND approved_at IS NULL AND decision_reason IS NULL) OR (status = 'UnderReview' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NULL AND approved_at IS NULL AND decision_reason IS NULL) OR (status = 'RecoveryReserved' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NOT NULL AND approved_at IS NOT NULL AND completed_by IS NULL AND completed_at IS NULL AND length(btrim(decision_reason)) > 0) OR (status = 'Rejected' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NULL AND approved_at IS NULL AND length(btrim(decision_reason)) > 0) OR (status IN ('TransferPending', 'NeedsReconciliation') AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NOT NULL AND approved_at IS NOT NULL AND completed_by IS NULL AND completed_at IS NULL AND length(btrim(decision_reason)) > 0) OR (status IN ('Completed', 'TransferConfirmedFailed', 'ReservationReleased') AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NOT NULL AND approved_at IS NOT NULL AND completed_by IS NOT NULL AND completed_at IS NOT NULL AND length(btrim(decision_reason)) > 0)");

            migrationBuilder.CreateIndex(
                name: "ux_refund_transfer_records_bank_reference",
                schema: "billing",
                table: "refund_transfer_records",
                column: "bank_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_refund_transfer_records_record_idempotency",
                schema: "billing",
                table: "refund_transfer_records",
                columns: new[] { "recorded_by", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_refund_transfer_records_refund_request_id",
                schema: "billing",
                table: "refund_transfer_records",
                column: "refund_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_refund_transfer_records_resolution_operation",
                schema: "billing",
                table: "refund_transfer_records",
                column: "resolution_operation_id",
                unique: true,
                filter: "resolution_operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_refund_transfer_records_verify_idempotency",
                schema: "billing",
                table: "refund_transfer_records",
                columns: new[] { "verified_by", "verification_idempotency_key" },
                unique: true,
                filter: "verification_idempotency_key IS NOT NULL");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION billing.guard_refund_transfer_record_immutability()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'REFUND_TRANSFER_RECORDS_ARE_IMMUTABLE'
                            USING ERRCODE = 'P0001';
                    END IF;

                    IF OLD.id IS DISTINCT FROM NEW.id OR
                       OLD.refund_request_id IS DISTINCT FROM NEW.refund_request_id OR
                       OLD.user_id IS DISTINCT FROM NEW.user_id OR
                       OLD.bank_reference IS DISTINCT FROM NEW.bank_reference OR
                       OLD.amount_vnd IS DISTINCT FROM NEW.amount_vnd OR
                       OLD.evidence_reference IS DISTINCT FROM NEW.evidence_reference OR
                       OLD.recorded_by IS DISTINCT FROM NEW.recorded_by OR
                       OLD.recorded_at IS DISTINCT FROM NEW.recorded_at OR
                       OLD.idempotency_key IS DISTINCT FROM NEW.idempotency_key OR
                       OLD.request_fingerprint IS DISTINCT FROM NEW.request_fingerprint OR
                       OLD.verified_at IS NOT NULL OR NEW.verified_at IS NULL OR
                       NOT ((OLD.status = 'ReportedSucceeded' AND NEW.status = 'VerifiedSucceeded') OR
                            (OLD.status = 'ReportedFailed' AND NEW.status = 'VerifiedFailed') OR
                            (OLD.status = 'NeedsReconciliation' AND
                             NEW.status IN ('VerifiedSucceeded','VerifiedFailed'))) THEN
                        RAISE EXCEPTION 'REFUND_TRANSFER_RECORDS_ARE_IMMUTABLE'
                            USING ERRCODE = 'P0001';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER tr_refund_transfer_record_immutable
                BEFORE UPDATE OR DELETE ON billing.refund_transfer_records
                FOR EACH ROW EXECUTE FUNCTION billing.guard_refund_transfer_record_immutability();

                CREATE FUNCTION billing.assert_refund_payout_integrity(p_refund_id uuid)
                RETURNS void LANGUAGE plpgsql AS $function$
                DECLARE
                    request_row billing.refund_requests%ROWTYPE;
                    transfer_row billing.refund_transfer_records%ROWTYPE;
                    reservation_row wallet.coin_reservations%ROWTYPE;
                    v_operation_type text;
                    entry_count bigint;
                    available_delta numeric;
                    reserved_delta numeric;
                    lot_refunded bigint;
                    lot_reserved bigint;
                BEGIN
                    SELECT * INTO transfer_row FROM billing.refund_transfer_records
                    WHERE refund_request_id = p_refund_id;
                    IF NOT FOUND THEN RETURN; END IF;
                    SELECT * INTO request_row FROM billing.refund_requests WHERE id = p_refund_id;
                    SELECT * INTO reservation_row FROM wallet.coin_reservations
                    WHERE refund_request_id = p_refund_id;
                    IF request_row.id IS NULL OR reservation_row.id IS NULL OR
                       transfer_row.user_id <> request_row.user_id OR
                       transfer_row.amount_vnd <> request_row.net_refund_vnd OR
                       reservation_row.user_id <> request_row.user_id OR
                       reservation_row.amount_coins <> request_row.required_recovery_coins THEN
                        RAISE EXCEPTION 'RF03_REFUND_PAYOUT_INTEGRITY_CONFLICT'
                            USING ERRCODE = '23514';
                    END IF;

                    IF request_row.status = 'TransferPending' THEN
                        IF transfer_row.status NOT IN ('ReportedSucceeded','ReportedFailed') OR
                           reservation_row.status <> 'Reserved' THEN
                            RAISE EXCEPTION 'RF03_REFUND_PAYOUT_INTEGRITY_CONFLICT'
                                USING ERRCODE = '23514';
                        END IF;
                        RETURN;
                    ELSIF request_row.status = 'NeedsReconciliation' THEN
                        IF transfer_row.status <> 'NeedsReconciliation' OR
                           reservation_row.status <> 'Reserved' THEN
                            RAISE EXCEPTION 'RF03_REFUND_PAYOUT_INTEGRITY_CONFLICT'
                                USING ERRCODE = '23514';
                        END IF;
                        RETURN;
                    ELSIF request_row.status NOT IN ('Completed','ReservationReleased') THEN
                        RAISE EXCEPTION 'RF03_REFUND_PAYOUT_INTEGRITY_CONFLICT'
                            USING ERRCODE = '23514';
                    END IF;

                    SELECT o.operation_type INTO v_operation_type FROM wallet.wallet_operations o
                    WHERE id = transfer_row.resolution_operation_id
                      AND user_id = request_row.user_id
                      AND source_type = 'RefundRequest' AND source_id = request_row.id;
                    SELECT count(*),
                           COALESCE(sum(delta_coins) FILTER (WHERE bucket='Available'),0),
                           COALESCE(sum(delta_coins) FILTER (WHERE bucket='Reserved'),0)
                    INTO entry_count, available_delta, reserved_delta
                    FROM wallet.wallet_entries
                    WHERE operation_id = transfer_row.resolution_operation_id
                      AND user_id = request_row.user_id AND coin_type = 'Paid';
                    SELECT refunded_coins, reserved_coins INTO lot_refunded, lot_reserved
                    FROM wallet.paid_coin_lots WHERE id = reservation_row.paid_coin_lot_id;

                    IF request_row.status = 'Completed' AND
                       (transfer_row.status <> 'VerifiedSucceeded' OR
                        reservation_row.status <> 'Consumed' OR
                        v_operation_type IS DISTINCT FROM 'RefundComplete' OR entry_count <> 1 OR
                        available_delta <> 0 OR reserved_delta <> -reservation_row.amount_coins OR
                        lot_refunded <> reservation_row.amount_coins OR lot_reserved <> 0) THEN
                        RAISE EXCEPTION 'RF03_REFUND_PAYOUT_INTEGRITY_CONFLICT'
                            USING ERRCODE = '23514';
                    ELSIF request_row.status = 'ReservationReleased' AND
                       (transfer_row.status <> 'VerifiedFailed' OR
                        reservation_row.status <> 'Released' OR
                        v_operation_type IS DISTINCT FROM 'RefundRelease' OR entry_count <> 2 OR
                        available_delta <> reservation_row.amount_coins OR
                        reserved_delta <> -reservation_row.amount_coins OR
                        lot_refunded <> 0 OR lot_reserved <> 0) THEN
                        RAISE EXCEPTION 'RF03_REFUND_PAYOUT_INTEGRITY_CONFLICT'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $function$;

                CREATE FUNCTION billing.refund_payout_transfer_integrity_trigger()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    PERFORM billing.assert_refund_payout_integrity(NEW.refund_request_id);
                    RETURN NULL;
                END;
                $function$;

                CREATE FUNCTION billing.refund_payout_request_integrity_trigger()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    PERFORM billing.assert_refund_payout_integrity(NEW.id);
                    RETURN NULL;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER tr_refund_payout_transfer_integrity
                AFTER INSERT OR UPDATE ON billing.refund_transfer_records
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                EXECUTE FUNCTION billing.refund_payout_transfer_integrity_trigger();
                CREATE CONSTRAINT TRIGGER tr_refund_payout_request_integrity
                AFTER UPDATE ON billing.refund_requests
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                WHEN (NEW.status IN ('TransferPending','NeedsReconciliation','Completed','ReservationReleased'))
                EXECUTE FUNCTION billing.refund_payout_request_integrity_trigger();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS tr_refund_payout_request_integrity ON billing.refund_requests;
                DROP TRIGGER IF EXISTS tr_refund_payout_transfer_integrity ON billing.refund_transfer_records;
                DROP TRIGGER IF EXISTS tr_refund_transfer_record_immutable ON billing.refund_transfer_records;
                DROP FUNCTION IF EXISTS billing.refund_payout_request_integrity_trigger();
                DROP FUNCTION IF EXISTS billing.refund_payout_transfer_integrity_trigger();
                DROP FUNCTION IF EXISTS billing.assert_refund_payout_integrity(uuid);
                DROP FUNCTION IF EXISTS billing.guard_refund_transfer_record_immutability();
                """);

            migrationBuilder.DropTable(
                name: "refund_transfer_records",
                schema: "billing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refund_requests_review_lifecycle",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refund_requests_review_lifecycle",
                schema: "billing",
                table: "refund_requests",
                sql: "(status = 'Requested' AND reviewed_by IS NULL AND reviewed_at IS NULL AND approved_by IS NULL AND approved_at IS NULL AND decision_reason IS NULL) OR (status = 'UnderReview' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NULL AND approved_at IS NULL AND decision_reason IS NULL) OR (status = 'RecoveryReserved' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NOT NULL AND approved_at IS NOT NULL AND length(btrim(decision_reason)) > 0) OR (status = 'Rejected' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NULL AND approved_at IS NULL AND length(btrim(decision_reason)) > 0) OR status IN ('TransferPending', 'Completed', 'NeedsReconciliation', 'TransferConfirmedFailed', 'ReservationReleased')");
        }
    }
}
