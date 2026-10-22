using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenRefundRecoveryReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "approval_result_decision_note",
                schema: "wallet",
                table: "coin_reservations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approval_result_reviewed_at",
                schema: "wallet",
                table: "coin_reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approval_result_reviewed_by",
                schema: "wallet",
                table: "coin_reservations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approval_result_status",
                schema: "wallet",
                table: "coin_reservations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "approval_result_version",
                schema: "wallet",
                table: "coin_reservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM wallet.coin_reservations reservation
                        JOIN billing.refund_requests request
                          ON request.id = reservation.refund_request_id
                        WHERE request.reviewed_by IS NULL
                           OR request.reviewed_at IS NULL
                           OR request.decision_reason IS NULL
                           OR length(btrim(request.decision_reason)) = 0
                           OR request.version < 1
                    ) THEN
                        RAISE EXCEPTION 'RF02_APPROVAL_RESULT_BACKFILL_PRECONDITION_FAILED';
                    END IF;
                END;
                $block$;

                UPDATE wallet.coin_reservations reservation
                SET approval_result_status = 'RecoveryReserved',
                    approval_result_version = request.version,
                    approval_result_reviewed_by = request.reviewed_by,
                    approval_result_reviewed_at = request.reviewed_at,
                    approval_result_decision_note = request.decision_reason
                FROM billing.refund_requests request
                WHERE request.id = reservation.refund_request_id;

                SET CONSTRAINTS ALL IMMEDIATE;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "approval_result_decision_note",
                schema: "wallet",
                table: "coin_reservations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "approval_result_reviewed_at",
                schema: "wallet",
                table: "coin_reservations",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "approval_result_reviewed_by",
                schema: "wallet",
                table: "coin_reservations",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "approval_result_status",
                schema: "wallet",
                table: "coin_reservations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "approval_result_version",
                schema: "wallet",
                table: "coin_reservations",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_coin_reservations_approval_result_reviewed_by",
                schema: "wallet",
                table: "coin_reservations",
                column: "approval_result_reviewed_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_coin_reservations_approval_result",
                schema: "wallet",
                table: "coin_reservations",
                sql: "approval_result_status = 'RecoveryReserved' AND approval_result_version > 0 AND length(btrim(approval_result_decision_note)) > 0 AND approval_result_decision_note = btrim(approval_result_decision_note)");

            migrationBuilder.AddForeignKey(
                name: "fk_coin_reservations_result_reviewed_by_user",
                schema: "wallet",
                table: "coin_reservations",
                column: "approval_result_reviewed_by",
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
                    operation_available_delta numeric;
                    operation_reserved_delta numeric;
                    wallet_available numeric;
                    wallet_reserved numeric;
                    lot_available numeric;
                    lot_reserved numeric;
                    ledger_available numeric;
                    ledger_reserved numeric;
                    original_lot_reserved numeric;
                BEGIN
                    SELECT * INTO reservation
                    FROM wallet.coin_reservations
                    WHERE id = reservation_id;
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
                          AND request.reviewed_by = reservation.approval_result_reviewed_by
                          AND request.reviewed_at = reservation.approval_result_reviewed_at
                          AND reservation.approval_result_status = 'RecoveryReserved'
                          AND reservation.approval_result_version > 0
                          AND reservation.approval_result_decision_note IS NOT NULL
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
                    INTO entry_count, operation_available_delta, operation_reserved_delta
                    FROM wallet.wallet_entries
                    WHERE operation_id = reservation.wallet_operation_id
                      AND user_id = reservation.user_id;

                    SELECT paid_available, paid_reserved
                    INTO wallet_available, wallet_reserved
                    FROM wallet.wallets
                    WHERE user_id = reservation.user_id;

                    SELECT COALESCE(sum(granted_coins - spent_coins - refunded_coins - reserved_coins), 0),
                           COALESCE(sum(reserved_coins), 0)
                    INTO lot_available, lot_reserved
                    FROM wallet.paid_coin_lots
                    WHERE user_id = reservation.user_id;

                    SELECT COALESCE(sum(delta_coins) FILTER (
                               WHERE coin_type = 'Paid' AND bucket = 'Available'), 0),
                           COALESCE(sum(delta_coins) FILTER (
                               WHERE coin_type = 'Paid' AND bucket = 'Reserved'), 0)
                    INTO ledger_available, ledger_reserved
                    FROM wallet.wallet_entries
                    WHERE user_id = reservation.user_id;

                    SELECT reserved_coins INTO original_lot_reserved
                    FROM wallet.paid_coin_lots
                    WHERE id = reservation.paid_coin_lot_id
                      AND user_id = reservation.user_id;

                    IF NOT valid_relation OR entry_count <> 2 OR
                       operation_available_delta <> -reservation.amount_coins OR
                       operation_reserved_delta <> reservation.amount_coins OR
                       wallet_available IS NULL OR wallet_reserved IS NULL OR
                       wallet_available <> lot_available OR wallet_reserved <> lot_reserved OR
                       wallet_available <> ledger_available OR wallet_reserved <> ledger_reserved OR
                       (reservation.status = 'Reserved' AND
                        original_lot_reserved <> reservation.amount_coins) THEN
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
                        PERFORM wallet.assert_refund_reservation_integrity(NEW.id);
                    ELSE
                        FOR reservation_id IN
                            SELECT id
                            FROM wallet.coin_reservations
                            WHERE user_id = NEW.user_id AND status = 'Reserved'
                        LOOP
                            PERFORM wallet.assert_refund_reservation_integrity(reservation_id);
                        END LOOP;
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
                       NEW.approval_result_status IS DISTINCT FROM OLD.approval_result_status OR
                       NEW.approval_result_version IS DISTINCT FROM OLD.approval_result_version OR
                       NEW.approval_result_reviewed_by IS DISTINCT FROM OLD.approval_result_reviewed_by OR
                       NEW.approval_result_reviewed_at IS DISTINCT FROM OLD.approval_result_reviewed_at OR
                       NEW.approval_result_decision_note IS DISTINCT FROM OLD.approval_result_decision_note OR
                       NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                        RAISE EXCEPTION 'coin reservation snapshot fields are immutable';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER trg_wallets_refund_reservation_integrity
                AFTER INSERT OR UPDATE ON wallet.wallets
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION wallet.refund_reservation_integrity_trigger();
                CREATE CONSTRAINT TRIGGER trg_paid_coin_lots_refund_reservation_integrity
                AFTER INSERT OR UPDATE ON wallet.paid_coin_lots
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION wallet.refund_reservation_integrity_trigger();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_paid_coin_lots_refund_reservation_integrity
                    ON wallet.paid_coin_lots;
                DROP TRIGGER IF EXISTS trg_wallets_refund_reservation_integrity
                    ON wallet.wallets;

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
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_coin_reservations_result_reviewed_by_user",
                schema: "wallet",
                table: "coin_reservations");

            migrationBuilder.DropIndex(
                name: "IX_coin_reservations_approval_result_reviewed_by",
                schema: "wallet",
                table: "coin_reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_coin_reservations_approval_result",
                schema: "wallet",
                table: "coin_reservations");

            migrationBuilder.DropColumn(
                name: "approval_result_decision_note",
                schema: "wallet",
                table: "coin_reservations");

            migrationBuilder.DropColumn(
                name: "approval_result_reviewed_at",
                schema: "wallet",
                table: "coin_reservations");

            migrationBuilder.DropColumn(
                name: "approval_result_reviewed_by",
                schema: "wallet",
                table: "coin_reservations");

            migrationBuilder.DropColumn(
                name: "approval_result_status",
                schema: "wallet",
                table: "coin_reservations");

            migrationBuilder.DropColumn(
                name: "approval_result_version",
                schema: "wallet",
                table: "coin_reservations");
        }
    }
}
