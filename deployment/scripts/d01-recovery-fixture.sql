\set ON_ERROR_STOP on

BEGIN;

INSERT INTO identity.users (
    id, display_name, email, avatar_url, role, status,
    created_at, updated_at, version)
VALUES (
    'd0100000-0000-4000-8000-000000000001',
    'D01 Recovery Fixture',
    'd01-recovery@example.invalid',
    NULL,
    'User',
    'Active',
    '2026-01-01T00:00:00Z',
    '2026-01-01T00:00:00Z',
    1);

INSERT INTO billing.payment_orders (
    id, user_id, order_code, order_type, amount_vnd,
    paid_coins_snapshot, vip_plan_id, vip_duration_hours_snapshot,
    vip_discount_percent_snapshot, provider, provider_payment_status,
    fulfillment_status, idempotency_key, request_fingerprint, expires_at,
    verified_paid_at, fulfilled_at, created_at, version, expired_at,
    order_status)
VALUES (
    'd0100000-0000-4000-8000-000000000002',
    'd0100000-0000-4000-8000-000000000001',
    'D01-RECOVERY-TOPUP',
    'TopUp',
    10000,
    1000,
    NULL,
    NULL,
    NULL,
    'SePay',
    'Verified',
    'Fulfilled',
    'd01-recovery-idempotency',
    repeat('a', 64),
    '2026-01-01T00:30:00Z',
    '2026-01-01T00:01:00Z',
    '2026-01-01T00:01:00Z',
    '2026-01-01T00:00:00Z',
    1,
    NULL,
    'Succeeded');

INSERT INTO wallet.wallets (
    user_id, paid_available, paid_reserved, bonus_available,
    version, created_at, updated_at)
VALUES (
    'd0100000-0000-4000-8000-000000000001',
    1000,
    0,
    0,
    1,
    '2026-01-01T00:01:00Z',
    '2026-01-01T00:01:00Z');

INSERT INTO wallet.wallet_operations (
    id, user_id, operation_type, source_type, source_id,
    idempotency_key, created_at)
VALUES (
    'd0100000-0000-4000-8000-000000000003',
    'd0100000-0000-4000-8000-000000000001',
    'TopUpCredit',
    'PaymentOrder',
    'd0100000-0000-4000-8000-000000000002',
    'd01-recovery-credit',
    '2026-01-01T00:01:00Z');

INSERT INTO wallet.paid_coin_lots (
    id, user_id, topup_order_id, granted_coins, spent_coins,
    refunded_coins, reserved_coins, created_at, version,
    topup_order_type)
VALUES (
    'd0100000-0000-4000-8000-000000000004',
    'd0100000-0000-4000-8000-000000000001',
    'd0100000-0000-4000-8000-000000000002',
    1000,
    0,
    0,
    0,
    '2026-01-01T00:01:00Z',
    1,
    'TopUp');

INSERT INTO wallet.wallet_entries (
    id, operation_id, user_id, coin_type, bucket,
    delta_coins, leg_order, created_at)
VALUES (
    'd0100000-0000-4000-8000-000000000005',
    'd0100000-0000-4000-8000-000000000003',
    'd0100000-0000-4000-8000-000000000001',
    'Paid',
    'Available',
    1000,
    1,
    '2026-01-01T00:01:00Z');

INSERT INTO infrastructure.outbox_messages (
    id, event_type, payload, occurred_at, available_at,
    status, attempt_count, locked_until, last_error_redacted)
VALUES (
    'd0100000-0000-4000-8000-000000000006',
    'TopUpFulfilled',
    '{"orderId":"d0100000-0000-4000-8000-000000000002","amountVnd":10000,"creditedPaidCoins":1000}'::jsonb,
    '2026-01-01T00:01:00Z',
    '2026-01-01T00:01:00Z',
    'Completed',
    1,
    NULL,
    NULL);

INSERT INTO billing.provider_transactions (
    id, provider, provider_transaction_id, payload_fingerprint,
    amount_vnd, direction, payment_code, account_number_last4,
    reference_code, matched_order_id, occurred_at, verified_at,
    processing_status, reconciliation_reason)
VALUES (
    'd0100000-0000-4000-8000-000000000007',
    'SePay',
    'D01-PROVIDER-TRANSACTION',
    repeat('b', 64),
    10000,
    'Incoming',
    'D01-RECOVERY-TOPUP',
    '6789',
    'D01-RECOVERY-REFERENCE',
    'd0100000-0000-4000-8000-000000000002',
    '2026-01-01T00:00:30Z',
    '2026-01-01T00:01:00Z',
    'Fulfilled',
    NULL);

INSERT INTO billing.webhook_deliveries (
    id, provider_transaction_id, received_at, auth_verified,
    payload_reference, delivery_reference, processing_result, error_code)
VALUES (
    'd0100000-0000-4000-8000-000000000008',
    'd0100000-0000-4000-8000-000000000007',
    '2026-01-01T00:01:00Z',
    TRUE,
    repeat('c', 64),
    'D01-WEBHOOK-DELIVERY',
    'Accepted',
    NULL);

INSERT INTO billing.refund_requests (
    id, payment_order_id, user_id, payment_order_type, reason,
    original_amount_vnd, fee_amount_vnd, net_refund_vnd,
    required_recovery_coins, status, idempotency_key,
    request_fingerprint, request_fingerprint_version, requested_at,
    reviewed_by, reviewed_at, approved_by, approved_at,
    completed_by, completed_at, decision_reason, version)
VALUES (
    'd0100000-0000-4000-8000-000000000009',
    'd0100000-0000-4000-8000-000000000002',
    'd0100000-0000-4000-8000-000000000001',
    'TopUp',
    'D01 deterministic recovery fixture',
    10000,
    500,
    9500,
    1000,
    'RecoveryReserved',
    'd01-refund-request',
    repeat('d', 64),
    'v1',
    '2026-01-02T00:00:00Z',
    'd0100000-0000-4000-8000-000000000001',
    '2026-01-02T00:01:00Z',
    'd0100000-0000-4000-8000-000000000001',
    '2026-01-02T00:02:00Z',
    NULL,
    NULL,
    'Original lot verified and reserved',
    3);

INSERT INTO billing.refund_recipients (
    id, refund_request_id, bank_code, account_number_encrypted,
    account_holder_encrypted, account_number_last4,
    account_number_masked, account_holder_masked,
    encryption_key_version, created_at)
VALUES (
    'd0100000-0000-4000-8000-00000000000a',
    'd0100000-0000-4000-8000-000000000009',
    'D01BANK',
    decode('010203040506', 'hex'),
    decode('0708090a0b0c', 'hex'),
    '6789',
    '******6789',
    'D01 RECOVERY USER',
    'v1',
    '2026-01-02T00:00:00Z');

INSERT INTO wallet.wallet_operations (
    id, user_id, operation_type, source_type, source_id,
    idempotency_key, created_at)
VALUES (
    'd0100000-0000-4000-8000-00000000000b',
    'd0100000-0000-4000-8000-000000000001',
    'RefundReserve',
    'RefundRequest',
    'd0100000-0000-4000-8000-000000000009',
    'd01-refund-reserve-operation',
    '2026-01-02T00:02:00Z');

INSERT INTO wallet.wallet_entries (
    id, operation_id, user_id, coin_type, bucket,
    delta_coins, leg_order, created_at)
VALUES
    ('d0100000-0000-4000-8000-00000000000c',
     'd0100000-0000-4000-8000-00000000000b',
     'd0100000-0000-4000-8000-000000000001',
     'Paid', 'Available', -1000, 1, '2026-01-02T00:02:00Z'),
    ('d0100000-0000-4000-8000-00000000000d',
     'd0100000-0000-4000-8000-00000000000b',
     'd0100000-0000-4000-8000-000000000001',
     'Paid', 'Reserved', 1000, 2, '2026-01-02T00:02:00Z');

UPDATE wallet.wallets
SET paid_available = 0,
    paid_reserved = 1000,
    version = 2,
    updated_at = '2026-01-02T00:02:00Z'
WHERE user_id = 'd0100000-0000-4000-8000-000000000001';

UPDATE wallet.paid_coin_lots
SET reserved_coins = 1000,
    version = 2
WHERE id = 'd0100000-0000-4000-8000-000000000004';

INSERT INTO wallet.coin_reservations (
    id, refund_request_id, user_id, paid_coin_lot_id,
    wallet_operation_id, approved_by, idempotency_key,
    request_fingerprint, amount_coins, approval_result_status,
    approval_result_version, approval_result_reviewed_by,
    approval_result_reviewed_at, approval_result_decision_note,
    status, created_at, resolved_at)
VALUES (
    'd0100000-0000-4000-8000-00000000000e',
    'd0100000-0000-4000-8000-000000000009',
    'd0100000-0000-4000-8000-000000000001',
    'd0100000-0000-4000-8000-000000000004',
    'd0100000-0000-4000-8000-00000000000b',
    'd0100000-0000-4000-8000-000000000001',
    'd01-refund-reservation',
    repeat('e', 64),
    1000,
    'RecoveryReserved',
    3,
    'd0100000-0000-4000-8000-000000000001',
    '2026-01-02T00:01:00Z',
    'Original lot verified and reserved',
    'Reserved',
    '2026-01-02T00:02:00Z',
    NULL);

INSERT INTO administration.audit_logs (
    id, actor_id, action, target_type, target_id,
    correlation_id, occurred_at, redacted_changes)
VALUES (
    'd0100000-0000-4000-8000-00000000000f',
    'd0100000-0000-4000-8000-000000000001',
    'Billing.RefundRecoveryReserved',
    'RefundRequest',
    'd0100000-0000-4000-8000-000000000009',
    'd01-recovery-fixture',
    '2026-01-02T00:02:00Z',
    '{"status":"RecoveryReserved","amountCoins":1000}'::jsonb);

INSERT INTO infrastructure.outbox_messages (
    id, event_type, payload, occurred_at, available_at,
    status, attempt_count, locked_until, last_error_redacted)
VALUES (
    'd0100000-0000-4000-8000-000000000010',
    'Billing.RefundRecoveryReserved.v1',
    '{"refundId":"d0100000-0000-4000-8000-000000000009","reservationId":"d0100000-0000-4000-8000-00000000000e","paidCoinLotId":"d0100000-0000-4000-8000-000000000004","walletOperationId":"d0100000-0000-4000-8000-00000000000b","amountCoins":1000}'::jsonb,
    '2026-01-02T00:02:00Z',
    '2026-01-02T00:02:00Z',
    'Pending',
    0,
    NULL,
    NULL);

COMMIT;
