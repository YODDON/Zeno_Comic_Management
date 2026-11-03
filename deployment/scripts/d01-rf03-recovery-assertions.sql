\set ON_ERROR_STOP on

DO $verification$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM wallet.coin_reservations
        WHERE id = 'd0100000-0000-4000-8000-00000000000e'
          AND status = 'Reserved' AND resolved_at IS NULL
    ) OR NOT EXISTS (
        SELECT 1
        FROM wallet.coin_reservations reservation
        JOIN billing.refund_transfer_records transfer
          ON transfer.refund_request_id = reservation.refund_request_id
        JOIN billing.refund_requests refund ON refund.id = reservation.refund_request_id
        JOIN wallet.wallet_operations operation ON operation.id = transfer.resolution_operation_id
        WHERE reservation.id = 'd0200000-0000-4000-8000-00000000000e'
          AND reservation.status = 'Consumed'
          AND refund.status = 'Completed'
          AND transfer.status = 'VerifiedSucceeded'
          AND operation.operation_type = 'RefundComplete'
    ) OR NOT EXISTS (
        SELECT 1
        FROM wallet.coin_reservations reservation
        JOIN billing.refund_transfer_records transfer
          ON transfer.refund_request_id = reservation.refund_request_id
        JOIN billing.refund_requests refund ON refund.id = reservation.refund_request_id
        JOIN wallet.wallet_operations operation ON operation.id = transfer.resolution_operation_id
        WHERE reservation.id = 'd0300000-0000-4000-8000-00000000000e'
          AND reservation.status = 'Released'
          AND refund.status = 'ReservationReleased'
          AND transfer.status = 'VerifiedFailed'
          AND operation.operation_type = 'RefundRelease'
    ) THEN
        RAISE EXCEPTION 'D01 RF03 Reserved/Consumed/Released recovery fixtures do not match';
    END IF;
END
$verification$;

SELECT 'd01_rf03_recovery_fixtures_passed' AS result;
