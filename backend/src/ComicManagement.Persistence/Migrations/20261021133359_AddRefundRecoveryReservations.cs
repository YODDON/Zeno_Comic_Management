using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundRecoveryReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_paid_coin_lots_id_user_id",
                schema: "wallet",
                table: "paid_coin_lots",
                columns: new[] { "id", "user_id" });

            migrationBuilder.CreateTable(
                name: "coin_reservations",
                schema: "wallet",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paid_coin_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount_coins = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coin_reservations", x => x.id);
                    table.CheckConstraint("ck_coin_reservations_amount", "amount_coins > 0");
                    table.CheckConstraint("ck_coin_reservations_idempotency", "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key) AND request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_coin_reservations_resolution", "(status = 'Reserved' AND resolved_at IS NULL) OR (status IN ('Consumed', 'Released') AND resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_coin_reservations_status", "status IN ('Reserved', 'Consumed', 'Released')");
                    table.ForeignKey(
                        name: "fk_coin_reservations_approved_by_user",
                        column: x => x.approved_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_coin_reservations_paid_lot_owner",
                        columns: x => new { x.paid_coin_lot_id, x.user_id },
                        principalSchema: "wallet",
                        principalTable: "paid_coin_lots",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_coin_reservations_refund_request_owner",
                        columns: x => new { x.refund_request_id, x.user_id },
                        principalSchema: "billing",
                        principalTable: "refund_requests",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_coin_reservations_wallet_operation_owner",
                        columns: x => new { x.wallet_operation_id, x.user_id },
                        principalSchema: "wallet",
                        principalTable: "wallet_operations",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_approved_by",
                schema: "billing",
                table: "refund_requests",
                column: "approved_by");

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_completed_by",
                schema: "billing",
                table: "refund_requests",
                column: "completed_by");

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_reviewed_by",
                schema: "billing",
                table: "refund_requests",
                column: "reviewed_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refund_requests_review_lifecycle",
                schema: "billing",
                table: "refund_requests",
                sql: "(status = 'Requested' AND reviewed_by IS NULL AND reviewed_at IS NULL AND approved_by IS NULL AND approved_at IS NULL AND decision_reason IS NULL) OR (status = 'UnderReview' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NULL AND approved_at IS NULL AND decision_reason IS NULL) OR (status = 'RecoveryReserved' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NOT NULL AND approved_at IS NOT NULL AND length(btrim(decision_reason)) > 0) OR (status = 'Rejected' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND approved_by IS NULL AND approved_at IS NULL AND length(btrim(decision_reason)) > 0) OR status IN ('TransferPending', 'Completed', 'NeedsReconciliation', 'TransferConfirmedFailed', 'ReservationReleased')");

            migrationBuilder.CreateIndex(
                name: "ix_coin_reservations_lot_status",
                schema: "wallet",
                table: "coin_reservations",
                columns: new[] { "paid_coin_lot_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_coin_reservations_approver_idempotency",
                schema: "wallet",
                table: "coin_reservations",
                columns: new[] { "approved_by", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_coin_reservations_refund_request_id",
                schema: "wallet",
                table: "coin_reservations",
                column: "refund_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_coin_reservations_wallet_operation_id",
                schema: "wallet",
                table: "coin_reservations",
                column: "wallet_operation_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_refund_requests_approved_by_user",
                schema: "billing",
                table: "refund_requests",
                column: "approved_by",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_refund_requests_completed_by_user",
                schema: "billing",
                table: "refund_requests",
                column: "completed_by",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_refund_requests_reviewed_by_user",
                schema: "billing",
                table: "refund_requests",
                column: "reviewed_by",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION wallet.assert_refund_reservation_integrity(reservation_id uuid)
                RETURNS void LANGUAGE plpgsql AS $function$
                DECLARE
                    reservation wallet.coin_reservations%ROWTYPE;
                    valid_relation boolean;
                    entry_count integer;
                    available_delta numeric;
                    reserved_delta numeric;
                BEGIN
                    SELECT * INTO reservation FROM wallet.coin_reservations WHERE id = reservation_id;
                    IF NOT FOUND THEN RETURN; END IF;

                    SELECT EXISTS (
                        SELECT 1
                        FROM billing.refund_requests request
                        JOIN wallet.paid_coin_lots lot
                          ON lot.id = reservation.paid_coin_lot_id
                         AND lot.user_id = reservation.user_id
                        JOIN wallet.wallet_operations operation
                          ON operation.id = reservation.wallet_operation_id
                         AND operation.user_id = reservation.user_id
                        WHERE request.id = reservation.refund_request_id
                          AND request.user_id = reservation.user_id
                          AND request.approved_by = reservation.approved_by
                          AND ((reservation.status = 'Reserved' AND request.status IN (
                                   'RecoveryReserved', 'TransferPending',
                                   'NeedsReconciliation', 'TransferConfirmedFailed'))
                               OR (reservation.status = 'Consumed' AND request.status = 'Completed')
                               OR (reservation.status = 'Released' AND request.status = 'ReservationReleased'))
                          AND request.required_recovery_coins = reservation.amount_coins
                          AND lot.topup_order_id = request.payment_order_id
                          AND lot.granted_coins = reservation.amount_coins
                          AND operation.operation_type = 'RefundReserve'
                          AND operation.source_type = 'RefundRequest'
                          AND operation.source_id = reservation.refund_request_id)
                    INTO valid_relation;

                    SELECT count(*),
                           COALESCE(sum(delta_coins) FILTER (
                               WHERE coin_type = 'Paid' AND bucket = 'Available'), 0),
                           COALESCE(sum(delta_coins) FILTER (
                               WHERE coin_type = 'Paid' AND bucket = 'Reserved'), 0)
                    INTO entry_count, available_delta, reserved_delta
                    FROM wallet.wallet_entries
                    WHERE operation_id = reservation.wallet_operation_id
                      AND user_id = reservation.user_id;

                    IF NOT valid_relation OR entry_count <> 2 OR
                       available_delta <> -reservation.amount_coins OR
                       reserved_delta <> reservation.amount_coins THEN
                        RAISE EXCEPTION 'RF02_REFUND_RESERVATION_INTEGRITY_CONFLICT'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $function$;

                CREATE OR REPLACE FUNCTION wallet.refund_reservation_integrity_trigger()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE reservation_id uuid;
                BEGIN
                    IF TG_TABLE_NAME = 'coin_reservations' THEN
                        reservation_id := NEW.id;
                    ELSE
                        SELECT id INTO reservation_id FROM wallet.coin_reservations
                        WHERE wallet_operation_id = NEW.operation_id;
                    END IF;
                    IF reservation_id IS NOT NULL THEN
                        PERFORM wallet.assert_refund_reservation_integrity(reservation_id);
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE OR REPLACE FUNCTION billing.refund_reserved_state_integrity_trigger()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW.status = 'RecoveryReserved' AND NOT EXISTS (
                        SELECT 1 FROM wallet.coin_reservations reservation
                        WHERE reservation.refund_request_id = NEW.id
                          AND reservation.user_id = NEW.user_id) THEN
                        RAISE EXCEPTION 'RF02_REFUND_RESERVED_WITHOUT_RESERVATION'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE OR REPLACE FUNCTION wallet.guard_coin_reservation_snapshot()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'coin reservation records cannot be deleted';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id OR
                       NEW.refund_request_id IS DISTINCT FROM OLD.refund_request_id OR
                       NEW.user_id IS DISTINCT FROM OLD.user_id OR
                       NEW.paid_coin_lot_id IS DISTINCT FROM OLD.paid_coin_lot_id OR
                       NEW.wallet_operation_id IS DISTINCT FROM OLD.wallet_operation_id OR
                       NEW.approved_by IS DISTINCT FROM OLD.approved_by OR
                       NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key OR
                       NEW.request_fingerprint IS DISTINCT FROM OLD.request_fingerprint OR
                       NEW.amount_coins IS DISTINCT FROM OLD.amount_coins OR
                       NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                        RAISE EXCEPTION 'coin reservation snapshot fields are immutable';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER trg_coin_reservations_financial_integrity
                AFTER INSERT OR UPDATE ON wallet.coin_reservations
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION wallet.refund_reservation_integrity_trigger();
                CREATE CONSTRAINT TRIGGER trg_wallet_entries_refund_reservation_integrity
                AFTER INSERT OR UPDATE ON wallet.wallet_entries
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION wallet.refund_reservation_integrity_trigger();
                CREATE CONSTRAINT TRIGGER trg_refund_requests_reserved_state_integrity
                AFTER INSERT OR UPDATE ON billing.refund_requests
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION billing.refund_reserved_state_integrity_trigger();
                CREATE TRIGGER trg_coin_reservations_snapshot_guard
                BEFORE UPDATE OR DELETE ON wallet.coin_reservations
                FOR EACH ROW EXECUTE FUNCTION wallet.guard_coin_reservation_snapshot();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_refund_requests_reserved_state_integrity
                    ON billing.refund_requests;
                DROP TRIGGER IF EXISTS trg_wallet_entries_refund_reservation_integrity
                    ON wallet.wallet_entries;
                DROP TRIGGER IF EXISTS trg_coin_reservations_financial_integrity
                    ON wallet.coin_reservations;
                DROP TRIGGER IF EXISTS trg_coin_reservations_snapshot_guard
                    ON wallet.coin_reservations;
                DROP FUNCTION IF EXISTS billing.refund_reserved_state_integrity_trigger();
                DROP FUNCTION IF EXISTS wallet.refund_reservation_integrity_trigger();
                DROP FUNCTION IF EXISTS wallet.assert_refund_reservation_integrity(uuid);
                DROP FUNCTION IF EXISTS wallet.guard_coin_reservation_snapshot();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_refund_requests_approved_by_user",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_refund_requests_completed_by_user",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_refund_requests_reviewed_by_user",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropTable(
                name: "coin_reservations",
                schema: "wallet");

            migrationBuilder.DropIndex(
                name: "IX_refund_requests_approved_by",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropIndex(
                name: "IX_refund_requests_completed_by",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropIndex(
                name: "IX_refund_requests_reviewed_by",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refund_requests_review_lifecycle",
                schema: "billing",
                table: "refund_requests");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_paid_coin_lots_id_user_id",
                schema: "wallet",
                table: "paid_coin_lots");
        }
    }
}
