\set ON_ERROR_STOP on

DO $verification$
DECLARE
    pending_migrations bigint;
    provider_count bigint;
    webhook_count bigint;
    refund_count bigint;
    reservation_count bigint;
    transfer_count bigint;
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
    ) THEN
        RAISE EXCEPTION 'Restored fulfilled provider transaction does not reconcile with its payment order';
    END IF;

    IF EXISTS (
        SELECT provider, provider_transaction_id
        FROM billing.provider_transactions
        GROUP BY provider, provider_transaction_id
        HAVING count(*) <> 1
     ) THEN
        RAISE EXCEPTION 'Restored provider transaction identity is not unique';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM billing.payment_orders payment
        WHERE payment.fulfillment_status = 'Fulfilled'
          AND NOT EXISTS (
              SELECT 1
              FROM billing.provider_transactions provider
              WHERE provider.matched_order_id = payment.id
                AND provider.processing_status = 'Fulfilled')
     ) THEN
        RAISE EXCEPTION 'Restored fulfilled payment order lacks matching fulfilled provider evidence';
    END IF;

    IF EXISTS (
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
        WHERE NOT EXISTS (
            SELECT 1
            FROM billing.refund_requests refund
            JOIN billing.refund_recipients recipient
              ON recipient.refund_request_id = refund.id
            JOIN wallet.paid_coin_lots lot
              ON lot.id = reservation.paid_coin_lot_id
             AND lot.user_id = reservation.user_id
             AND lot.topup_order_id = refund.payment_order_id
            JOIN wallet.wallet_operations reserve_operation
              ON reserve_operation.id = reservation.wallet_operation_id
             AND reserve_operation.user_id = reservation.user_id
             AND reserve_operation.operation_type = 'RefundReserve'
             AND reserve_operation.source_type = 'RefundRequest'
             AND reserve_operation.source_id = refund.id
            WHERE refund.id = reservation.refund_request_id
              AND refund.user_id = reservation.user_id
              AND refund.required_recovery_coins = reservation.amount_coins
              AND lot.granted_coins = reservation.amount_coins
              AND (SELECT count(*) FROM wallet.wallet_entries entry
                   WHERE entry.operation_id = reserve_operation.id
                     AND entry.user_id = reservation.user_id) = 2
              AND (SELECT coalesce(sum(entry.delta_coins), 0)
                   FROM wallet.wallet_entries entry
                   WHERE entry.operation_id = reserve_operation.id
                     AND entry.coin_type = 'Paid' AND entry.bucket = 'Available') = -reservation.amount_coins
              AND (SELECT coalesce(sum(entry.delta_coins), 0)
                   FROM wallet.wallet_entries entry
                   WHERE entry.operation_id = reserve_operation.id
                      AND entry.coin_type = 'Paid' AND entry.bucket = 'Reserved') = reservation.amount_coins)
    ) THEN
        RAISE EXCEPTION 'Restored RF03 reservation does not link to the refund, recipient, exact lot and reserve ledger';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM wallet.coin_reservations reservation
        JOIN billing.refund_requests refund ON refund.id = reservation.refund_request_id
        WHERE (reservation.status = 'Reserved' AND (
                   refund.status NOT IN ('RecoveryReserved','TransferPending','NeedsReconciliation','TransferConfirmedFailed')
                   OR reservation.resolved_at IS NOT NULL))
           OR (reservation.status = 'Consumed' AND (
                   refund.status <> 'Completed' OR reservation.resolved_at IS NULL
                   OR NOT EXISTS (
                       SELECT 1
                       FROM billing.refund_transfer_records transfer
                       JOIN wallet.wallet_operations operation
                         ON operation.id = transfer.resolution_operation_id
                        AND operation.user_id = reservation.user_id
                        AND operation.operation_type = 'RefundComplete'
                        AND operation.source_type = 'RefundRequest'
                        AND operation.source_id = refund.id
                       WHERE transfer.refund_request_id = refund.id
                         AND transfer.user_id = refund.user_id
                         AND transfer.status = 'VerifiedSucceeded'
                         AND (SELECT count(*) FROM wallet.wallet_entries entry
                              WHERE entry.operation_id = operation.id) = 1
                         AND (SELECT coalesce(sum(entry.delta_coins), 0)
                              FROM wallet.wallet_entries entry
                              WHERE entry.operation_id = operation.id
                                AND entry.coin_type = 'Paid' AND entry.bucket = 'Reserved') = -reservation.amount_coins
                         AND EXISTS (
                             SELECT 1 FROM infrastructure.outbox_messages message
                             WHERE message.event_type = 'Billing.RefundCompleted.v1'
                               AND message.payload ->> 'refundRequestId' = refund.id::text
                               AND message.payload ->> 'walletOperationId' = operation.id::text
                               AND message.payload ->> 'transferRecordId' = transfer.id::text))))
           OR (reservation.status = 'Released' AND (
                   refund.status <> 'ReservationReleased' OR reservation.resolved_at IS NULL
                   OR NOT EXISTS (
                       SELECT 1
                       FROM billing.refund_transfer_records transfer
                       JOIN wallet.wallet_operations operation
                         ON operation.id = transfer.resolution_operation_id
                        AND operation.user_id = reservation.user_id
                        AND operation.operation_type = 'RefundRelease'
                        AND operation.source_type = 'RefundRequest'
                        AND operation.source_id = refund.id
                       WHERE transfer.refund_request_id = refund.id
                         AND transfer.user_id = refund.user_id
                         AND transfer.status = 'VerifiedFailed'
                         AND (SELECT count(*) FROM wallet.wallet_entries entry
                              WHERE entry.operation_id = operation.id) = 2
                         AND (SELECT coalesce(sum(entry.delta_coins), 0)
                              FROM wallet.wallet_entries entry
                              WHERE entry.operation_id = operation.id
                                AND entry.coin_type = 'Paid' AND entry.bucket = 'Reserved') = -reservation.amount_coins
                         AND (SELECT coalesce(sum(entry.delta_coins), 0)
                              FROM wallet.wallet_entries entry
                              WHERE entry.operation_id = operation.id
                                AND entry.coin_type = 'Paid' AND entry.bucket = 'Available') = reservation.amount_coins
                         AND EXISTS (
                             SELECT 1 FROM infrastructure.outbox_messages message
                             WHERE message.event_type = 'Billing.RefundReservationReleased.v1'
                               AND message.payload ->> 'refundRequestId' = refund.id::text
                               AND message.payload ->> 'walletOperationId' = operation.id::text
                               AND message.payload ->> 'transferRecordId' = transfer.id::text))))
           OR reservation.status NOT IN ('Reserved','Consumed','Released')
     ) THEN
        RAISE EXCEPTION 'Restored RF03 reservation lifecycle is inconsistent with its terminal operation, ledger and outbox';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM billing.refund_transfer_records transfer
        WHERE NOT EXISTS (
            SELECT 1
            FROM billing.refund_requests refund
            JOIN billing.refund_recipients recipient ON recipient.refund_request_id = refund.id
            JOIN wallet.coin_reservations reservation ON reservation.refund_request_id = refund.id
            WHERE refund.id = transfer.refund_request_id
              AND refund.user_id = transfer.user_id
              AND transfer.amount_vnd = refund.net_refund_vnd
              AND reservation.user_id = refund.user_id)
           OR (transfer.status IN ('ReportedSucceeded','ReportedFailed','NeedsReconciliation')
               AND NOT EXISTS (
                   SELECT 1 FROM wallet.coin_reservations reservation
                   WHERE reservation.refund_request_id = transfer.refund_request_id
                      AND reservation.status = 'Reserved'))
     ) THEN
        RAISE EXCEPTION 'Restored RF03 transfer does not reconcile with refund, recipient and reservation state';
    END IF;

    IF EXISTS (
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
        RAISE EXCEPTION 'Restored RF03 transfer/reservation lifecycle does not reconcile with refund, recipient, exact lot, operations, ledger and outbox';
    END IF;
    SELECT count(*) INTO reservation_count FROM wallet.coin_reservations;
    SELECT count(*) INTO transfer_count FROM billing.refund_transfer_records;
    RAISE NOTICE 'RF03 evidence checked: coin_reservations=%, transfer_records=%',
        reservation_count, transfer_count;

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
