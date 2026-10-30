\set ON_ERROR_STOP on

DO $verification$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM billing.payment_orders payment
        JOIN wallet.paid_coin_lots lot
          ON lot.topup_order_id = payment.id
         AND lot.user_id = payment.user_id
        JOIN wallet.wallets wallet ON wallet.user_id = payment.user_id
        JOIN wallet.wallet_operations operation
          ON operation.source_id = payment.id
         AND operation.user_id = payment.user_id
         AND operation.operation_type = 'TopUpCredit'
         AND operation.source_type = 'PaymentOrder'
        JOIN wallet.wallet_entries entry
          ON entry.operation_id = operation.id
         AND entry.user_id = operation.user_id
        WHERE payment.id = 'd0100000-0000-4000-8000-000000000002'
          AND payment.order_status = 'Succeeded'
          AND payment.amount_vnd = 10000
          AND payment.paid_coins_snapshot = 1000
          AND lot.id = 'd0100000-0000-4000-8000-000000000004'
          AND lot.granted_coins = 1000
          AND lot.spent_coins = 0
          AND lot.refunded_coins = 0
          AND lot.reserved_coins = 1000
          AND wallet.paid_available = 0
          AND wallet.paid_reserved = 1000
          AND entry.id = 'd0100000-0000-4000-8000-000000000005'
          AND entry.coin_type = 'Paid'
          AND entry.bucket = 'Available'
          AND entry.delta_coins = 1000
    ) THEN
        RAISE EXCEPTION 'D01 restored payment, Wallet, lot and immutable ledger fixture does not match';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM infrastructure.outbox_messages
        WHERE id = 'd0100000-0000-4000-8000-000000000006'
          AND event_type = 'TopUpFulfilled'
          AND payload ->> 'orderId' = 'd0100000-0000-4000-8000-000000000002'
          AND payload ->> 'amountVnd' = '10000'
          AND payload ->> 'creditedPaidCoins' = '1000'
    ) THEN
        RAISE EXCEPTION 'D01 restored outbox fixture does not match';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM billing.provider_transactions provider
        JOIN billing.webhook_deliveries delivery
          ON delivery.provider_transaction_id = provider.id
        WHERE provider.id = 'd0100000-0000-4000-8000-000000000007'
          AND provider.matched_order_id = 'd0100000-0000-4000-8000-000000000002'
          AND provider.provider_transaction_id = 'D01-PROVIDER-TRANSACTION'
          AND provider.processing_status = 'Fulfilled'
          AND provider.amount_vnd = 10000
          AND delivery.id = 'd0100000-0000-4000-8000-000000000008'
          AND delivery.auth_verified
          AND delivery.processing_result = 'Accepted'
    ) THEN
        RAISE EXCEPTION 'D01 restored provider transaction and webhook evidence does not match';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM billing.refund_requests request
        JOIN billing.refund_recipients recipient
          ON recipient.refund_request_id = request.id
        JOIN wallet.coin_reservations reservation
          ON reservation.refund_request_id = request.id
         AND reservation.user_id = request.user_id
        JOIN wallet.paid_coin_lots lot
          ON lot.id = reservation.paid_coin_lot_id
         AND lot.topup_order_id = request.payment_order_id
         AND lot.user_id = request.user_id
        JOIN wallet.wallet_operations operation
          ON operation.id = reservation.wallet_operation_id
         AND operation.user_id = request.user_id
        WHERE request.id = 'd0100000-0000-4000-8000-000000000009'
          AND request.status = 'RecoveryReserved'
          AND request.original_amount_vnd = 10000
          AND request.fee_amount_vnd = 500
          AND request.net_refund_vnd = 9500
          AND request.required_recovery_coins = 1000
          AND recipient.id = 'd0100000-0000-4000-8000-00000000000a'
          AND recipient.encryption_key_version = 'v1'
          AND reservation.id = 'd0100000-0000-4000-8000-00000000000e'
          AND reservation.status = 'Reserved'
          AND reservation.amount_coins = 1000
          AND lot.id = 'd0100000-0000-4000-8000-000000000004'
          AND lot.reserved_coins = 1000
          AND operation.operation_type = 'RefundReserve'
          AND operation.source_type = 'RefundRequest'
          AND operation.source_id = request.id
    ) THEN
        RAISE EXCEPTION 'D01 restored refund recipient, exact original lot and reservation linkage does not match';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM wallet.wallet_entries
        WHERE operation_id = 'd0100000-0000-4000-8000-00000000000b'
        GROUP BY operation_id
        HAVING count(*) = 2
           AND sum(delta_coins) FILTER (WHERE coin_type = 'Paid' AND bucket = 'Available') = -1000
           AND sum(delta_coins) FILTER (WHERE coin_type = 'Paid' AND bucket = 'Reserved') = 1000
    ) THEN
        RAISE EXCEPTION 'D01 restored refund reservation immutable ledger does not reconcile';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM wallet.wallets wallet
        WHERE wallet.user_id = 'd0100000-0000-4000-8000-000000000001'
          AND wallet.paid_available = (
              SELECT sum(granted_coins - spent_coins - refunded_coins - reserved_coins)
              FROM wallet.paid_coin_lots
              WHERE user_id = wallet.user_id)
          AND wallet.paid_reserved = (
              SELECT sum(reserved_coins)
              FROM wallet.paid_coin_lots
              WHERE user_id = wallet.user_id)
          AND wallet.paid_available = (
              SELECT sum(delta_coins) FILTER (WHERE coin_type = 'Paid' AND bucket = 'Available')
              FROM wallet.wallet_entries
              WHERE user_id = wallet.user_id)
          AND wallet.paid_reserved = (
              SELECT sum(delta_coins) FILTER (WHERE coin_type = 'Paid' AND bucket = 'Reserved')
              FROM wallet.wallet_entries
              WHERE user_id = wallet.user_id)
    ) THEN
        RAISE EXCEPTION 'D01 restored Wallet, PaidCoinLot and immutable ledger aggregates do not reconcile';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM administration.audit_logs
        WHERE id = 'd0100000-0000-4000-8000-00000000000f'
          AND action = 'Billing.RefundRecoveryReserved'
          AND target_id = 'd0100000-0000-4000-8000-000000000009'
          AND redacted_changes ->> 'amountCoins' = '1000'
    ) OR NOT EXISTS (
        SELECT 1
        FROM infrastructure.outbox_messages
        WHERE id = 'd0100000-0000-4000-8000-000000000010'
          AND event_type = 'Billing.RefundRecoveryReserved.v1'
          AND payload ->> 'reservationId' = 'd0100000-0000-4000-8000-00000000000e'
          AND payload ->> 'amountCoins' = '1000'
    ) THEN
        RAISE EXCEPTION 'D01 restored refund audit/outbox evidence does not match';
    END IF;
END
$verification$;

SELECT 'd01_recovery_fixture_passed' AS result;
