\set ON_ERROR_STOP on

DO $verification$
DECLARE
    pending_migrations bigint;
BEGIN
    SELECT count(*) INTO pending_migrations
    FROM information_schema.tables
    WHERE table_schema = 'infrastructure'
      AND table_name = '__ef_migrations_history';

    IF pending_migrations <> 1 THEN
        RAISE EXCEPTION 'EF migration history is missing from the restored database';
    END IF;

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
    ) THEN
        RAISE EXCEPTION 'Restored Wallet Paid balances do not reconcile with PaidCoinLots';
    END IF;

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
END
$verification$;

SELECT 'restore_verification_passed' AS result;
