\set ON_ERROR_STOP on

DO $verification$
DECLARE
    pending_migrations bigint;
    provider_count bigint;
    webhook_count bigint;
    refund_count bigint;
    reservation_count bigint;
    outbox_count bigint;
    audit_count bigint;
BEGIN
    SELECT count(*) INTO pending_migrations
    FROM information_schema.tables
    WHERE table_schema = 'infrastructure'
      AND table_name = '__ef_migrations_history';

    IF pending_migrations <> 1 THEN
        RAISE EXCEPTION 'EF migration history is missing from the restored database';
    END IF;

    SELECT count(*) INTO provider_count FROM billing.provider_transactions;
    SELECT count(*) INTO webhook_count FROM billing.webhook_deliveries;
    IF EXISTS (
        SELECT 1
        FROM billing.provider_transactions provider
        WHERE provider.processing_status = 'Fulfilled'
          AND (provider.matched_order_id IS NULL OR NOT EXISTS (
              SELECT 1
              FROM billing.payment_orders payment
              WHERE payment.id = provider.matched_order_id
                AND payment.amount_vnd = provider.amount_vnd
                AND payment.order_status = 'Succeeded'
                AND payment.fulfillment_status = 'Fulfilled'))
    ) OR EXISTS (
        SELECT provider, provider_transaction_id
        FROM billing.provider_transactions
        GROUP BY provider, provider_transaction_id
        HAVING count(*) <> 1
    ) OR EXISTS (
        SELECT 1
        FROM billing.payment_orders payment
        WHERE payment.fulfillment_status = 'Fulfilled'
          AND NOT EXISTS (
              SELECT 1
              FROM billing.provider_transactions provider
              WHERE provider.matched_order_id = payment.id
                AND provider.processing_status = 'Fulfilled')
    ) OR EXISTS (
        SELECT 1
        FROM billing.webhook_deliveries delivery
        WHERE NOT delivery.auth_verified
           OR delivery.provider_transaction_id IS NULL
           OR NOT EXISTS (
               SELECT 1
               FROM billing.provider_transactions provider
               WHERE provider.id = delivery.provider_transaction_id)
    ) THEN
        RAISE EXCEPTION 'Restored provider/payment/webhook evidence is inconsistent';
    END IF;
    RAISE NOTICE 'provider/payment evidence checked: provider_transactions=%, webhook_deliveries=%',
        provider_count, webhook_count;

    IF EXISTS (
        SELECT 1
        FROM wallet.wallets wallet
        LEFT JOIN (
            SELECT user_id,
                   sum(granted_coins - spent_coins - refunded_coins - reserved_coins) AS paid_available,
                   sum(reserved_coins) AS paid_reserved
            FROM wallet.paid_coin_lots
            GROUP BY user_id
        ) lots ON lots.user_id = wallet.user_id
        WHERE wallet.paid_available <> coalesce(lots.paid_available, 0)
           OR wallet.paid_reserved <> coalesce(lots.paid_reserved, 0)
    ) OR EXISTS (
        SELECT 1
        FROM wallet.paid_coin_lots lot
        WHERE NOT EXISTS (
            SELECT 1 FROM wallet.wallets wallet WHERE wallet.user_id = lot.user_id)
    ) THEN
        RAISE EXCEPTION 'Restored Wallet Paid balances do not reconcile with PaidCoinLots';
    END IF;

    SELECT count(*) INTO refund_count FROM billing.refund_requests;
    IF EXISTS (
        SELECT 1
        FROM billing.refund_requests refund
        WHERE (SELECT count(*)
            FROM billing.payment_orders payment
            JOIN wallet.paid_coin_lots lot
              ON lot.topup_order_id = payment.id
             AND lot.user_id = payment.user_id
            WHERE payment.id = refund.payment_order_id
              AND payment.user_id = refund.user_id
              AND payment.order_type = 'TopUp'
              AND payment.amount_vnd = refund.original_amount_vnd
              AND payment.paid_coins_snapshot = refund.required_recovery_coins) <> 1
           OR (SELECT count(*)
               FROM billing.refund_recipients recipient
               WHERE recipient.refund_request_id = refund.id) <> 1
           OR EXISTS (
               SELECT 1
               FROM billing.refund_recipients recipient
               WHERE recipient.refund_request_id = refund.id
                 AND (octet_length(recipient.account_number_encrypted) = 0
                      OR octet_length(recipient.account_holder_encrypted) = 0
                      OR length(btrim(recipient.encryption_key_version)) = 0))
    ) THEN
        RAISE EXCEPTION 'Restored refund request/recipient/original-lot snapshot is inconsistent';
    END IF;
    RAISE NOTICE 'refund evidence checked: refund_requests=%', refund_count;

    IF EXISTS (
        SELECT 1
        FROM wallet.wallets wallet
        LEFT JOIN (
            SELECT user_id,
                   coalesce(sum(delta_coins) FILTER (
                       WHERE coin_type = 'Paid' AND bucket = 'Available'), 0) AS paid_available,
                   coalesce(sum(delta_coins) FILTER (
                       WHERE coin_type = 'Paid' AND bucket = 'Reserved'), 0) AS paid_reserved,
                   coalesce(sum(delta_coins) FILTER (
                       WHERE coin_type = 'Bonus' AND bucket = 'Available'), 0) AS bonus_available
            FROM wallet.wallet_entries
            GROUP BY user_id
        ) ledger ON ledger.user_id = wallet.user_id
        WHERE wallet.paid_available <> coalesce(ledger.paid_available, 0)
           OR wallet.paid_reserved <> coalesce(ledger.paid_reserved, 0)
           OR wallet.bonus_available <> coalesce(ledger.bonus_available, 0)
    ) OR EXISTS (
        SELECT 1
        FROM wallet.wallet_entries entry
        WHERE NOT EXISTS (
            SELECT 1 FROM wallet.wallets wallet WHERE wallet.user_id = entry.user_id)
    ) THEN
        RAISE EXCEPTION 'Restored Wallet balances do not reconcile with immutable ledger entries';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM wallet.coin_reservations reservation
        WHERE reservation.status = 'Reserved'
          AND NOT EXISTS (
              SELECT 1
              FROM billing.refund_requests refund
              JOIN wallet.paid_coin_lots lot
                ON lot.id = reservation.paid_coin_lot_id
               AND lot.user_id = reservation.user_id
               AND lot.topup_order_id = refund.payment_order_id
              JOIN wallet.wallet_operations operation
                ON operation.id = reservation.wallet_operation_id
               AND operation.user_id = reservation.user_id
               AND operation.operation_type = 'RefundReserve'
               AND operation.source_type = 'RefundRequest'
               AND operation.source_id = refund.id
              WHERE refund.id = reservation.refund_request_id
                AND refund.user_id = reservation.user_id
                AND refund.status IN (
                    'RecoveryReserved',
                    'TransferPending',
                    'NeedsReconciliation',
                    'TransferConfirmedFailed')
                AND refund.required_recovery_coins = reservation.amount_coins
                AND lot.granted_coins = reservation.amount_coins
                AND lot.reserved_coins >= reservation.amount_coins
                AND (SELECT count(*)
                     FROM wallet.wallet_entries entry
                     WHERE entry.operation_id = operation.id
                       AND entry.user_id = reservation.user_id) = 2
                AND (SELECT coalesce(sum(entry.delta_coins), 0)
                     FROM wallet.wallet_entries entry
                     WHERE entry.operation_id = operation.id
                       AND entry.coin_type = 'Paid'
                       AND entry.bucket = 'Available') = -reservation.amount_coins
                AND (SELECT coalesce(sum(entry.delta_coins), 0)
                     FROM wallet.wallet_entries entry
                     WHERE entry.operation_id = operation.id
                       AND entry.coin_type = 'Paid'
                       AND entry.bucket = 'Reserved') = reservation.amount_coins))
       OR EXISTS (
           SELECT 1
           FROM wallet.paid_coin_lots lot
           LEFT JOIN (
               SELECT paid_coin_lot_id, sum(amount_coins) AS amount_coins
               FROM wallet.coin_reservations
               WHERE status = 'Reserved'
               GROUP BY paid_coin_lot_id
           ) reservation ON reservation.paid_coin_lot_id = lot.id
           WHERE lot.reserved_coins <> coalesce(reservation.amount_coins, 0))
    THEN
        RAISE EXCEPTION 'Restored refund reservations do not reconcile with exact refund, operation, ledger and lot state';
    END IF;
    SELECT count(*) INTO reservation_count FROM wallet.coin_reservations;
    RAISE NOTICE 'reservation evidence checked: coin_reservations=%', reservation_count;

    SELECT count(*) INTO outbox_count FROM infrastructure.outbox_messages;
    IF EXISTS (
        SELECT 1
        FROM infrastructure.outbox_messages message
        WHERE jsonb_typeof(message.payload) IS DISTINCT FROM 'object'
           OR length(btrim(message.event_type)) = 0
           OR (message.event_type = 'Billing.WalletTopUpCompleted.v1' AND NOT EXISTS (
               SELECT 1
               FROM billing.payment_orders payment
               JOIN billing.provider_transactions provider
                 ON provider.id::text = message.payload ->> 'providerTransactionId'
               WHERE payment.id::text = message.payload ->> 'paymentOrderId'
                 AND provider.matched_order_id = payment.id
                 AND payment.user_id::text = message.payload ->> 'userId'
                 AND payment.amount_vnd::text = message.payload ->> 'amountVnd'
                 AND payment.paid_coins_snapshot::text = message.payload ->> 'paidCoins'))
           OR (message.event_type IN (
                 'Billing.RefundRecoveryReserved.v1',
                 'Billing.RefundCompleted.v1',
                 'Billing.RefundReservationReleased.v1') AND NOT EXISTS (
               SELECT 1
               FROM billing.refund_requests refund
               WHERE refund.id::text = message.payload ->> 'refundRequestId'
                 AND refund.user_id::text = message.payload ->> 'userId'
                 AND refund.payment_order_id::text = message.payload ->> 'paymentOrderId'))
    ) THEN
        RAISE EXCEPTION 'Restored outbox structure or aggregate ownership is inconsistent';
    END IF;
    RAISE NOTICE 'outbox evidence checked: outbox_messages=%', outbox_count;

    SELECT count(*) INTO audit_count FROM administration.audit_logs;
    IF EXISTS (
        SELECT 1
        FROM administration.audit_logs audit
        WHERE length(btrim(audit.action)) = 0
           OR length(btrim(audit.target_type)) = 0
           OR length(btrim(audit.correlation_id)) = 0
           OR jsonb_typeof(audit.redacted_changes) IS DISTINCT FROM 'object'
           OR NOT EXISTS (
               SELECT 1 FROM identity.users actor WHERE actor.id = audit.actor_id)
    ) THEN
        RAISE EXCEPTION 'Restored audit evidence is structurally invalid';
    END IF;
    RAISE NOTICE 'audit evidence checked: audit_logs=%', audit_count;
END
$verification$;

SELECT 'restore_verification_passed' AS result;
