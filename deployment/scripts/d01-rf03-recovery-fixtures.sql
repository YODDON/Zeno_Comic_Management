\set ON_ERROR_STOP on
BEGIN;

-- Two terminal RF03 lifecycles: successful payout consumes the reservation;
-- verified failure releases it back to Paid Available.
INSERT INTO identity.users (id, display_name, email, role, status, created_at, updated_at, version)
VALUES
 ('d0200000-0000-4000-8000-000000000001','D01 Completed Refund','d01-complete@example.invalid','User','Active','2026-01-01Z','2026-01-01Z',1),
 ('d0300000-0000-4000-8000-000000000001','D01 Released Refund','d01-release@example.invalid','User','Active','2026-01-01Z','2026-01-01Z',1);

INSERT INTO billing.payment_orders
 (id,user_id,order_code,order_type,amount_vnd,paid_coins_snapshot,provider,
  provider_payment_status,fulfillment_status,idempotency_key,request_fingerprint,
  expires_at,verified_paid_at,fulfilled_at,created_at,version,order_status)
VALUES
 ('d0200000-0000-4000-8000-000000000002','d0200000-0000-4000-8000-000000000001','D01-COMPLETE','TopUp',20000,2000,'SePay','Verified','Fulfilled','d01-complete-order',repeat('1',64),'2026-01-01T00:30Z','2026-01-01T00:01Z','2026-01-01T00:01Z','2026-01-01Z',1,'Succeeded'),
 ('d0300000-0000-4000-8000-000000000002','d0300000-0000-4000-8000-000000000001','D01-RELEASE','TopUp',30000,3000,'SePay','Verified','Fulfilled','d01-release-order',repeat('2',64),'2026-01-01T00:30Z','2026-01-01T00:01Z','2026-01-01T00:01Z','2026-01-01Z',1,'Succeeded');

INSERT INTO wallet.wallets
 (user_id,paid_available,paid_reserved,bonus_available,version,created_at,updated_at)
VALUES
 ('d0200000-0000-4000-8000-000000000001',0,0,0,3,'2026-01-01T00:01Z','2026-01-02T00:04Z'),
 ('d0300000-0000-4000-8000-000000000001',3000,0,0,3,'2026-01-01T00:01Z','2026-01-02T00:04Z');

INSERT INTO wallet.wallet_operations
 (id,user_id,operation_type,source_type,source_id,idempotency_key,created_at)
VALUES
 ('d0200000-0000-4000-8000-000000000003','d0200000-0000-4000-8000-000000000001','TopUpCredit','PaymentOrder','d0200000-0000-4000-8000-000000000002','d01-complete-credit','2026-01-01T00:01Z'),
 ('d0200000-0000-4000-8000-00000000000b','d0200000-0000-4000-8000-000000000001','RefundReserve','RefundRequest','d0200000-0000-4000-8000-000000000009','d01-complete-reserve','2026-01-02T00:02Z'),
 ('d0200000-0000-4000-8000-000000000010','d0200000-0000-4000-8000-000000000001','RefundComplete','RefundRequest','d0200000-0000-4000-8000-000000000009','d01-complete-resolution','2026-01-02T00:04Z'),
 ('d0300000-0000-4000-8000-000000000003','d0300000-0000-4000-8000-000000000001','TopUpCredit','PaymentOrder','d0300000-0000-4000-8000-000000000002','d01-release-credit','2026-01-01T00:01Z'),
 ('d0300000-0000-4000-8000-00000000000b','d0300000-0000-4000-8000-000000000001','RefundReserve','RefundRequest','d0300000-0000-4000-8000-000000000009','d01-release-reserve','2026-01-02T00:02Z'),
 ('d0300000-0000-4000-8000-000000000010','d0300000-0000-4000-8000-000000000001','RefundRelease','RefundRequest','d0300000-0000-4000-8000-000000000009','d01-release-resolution','2026-01-02T00:04Z');

INSERT INTO wallet.paid_coin_lots
 (id,user_id,topup_order_id,granted_coins,spent_coins,refunded_coins,reserved_coins,created_at,version,topup_order_type)
VALUES
 ('d0200000-0000-4000-8000-000000000004','d0200000-0000-4000-8000-000000000001','d0200000-0000-4000-8000-000000000002',2000,0,2000,0,'2026-01-01T00:01Z',3,'TopUp'),
 ('d0300000-0000-4000-8000-000000000004','d0300000-0000-4000-8000-000000000001','d0300000-0000-4000-8000-000000000002',3000,0,0,0,'2026-01-01T00:01Z',3,'TopUp');

INSERT INTO wallet.wallet_entries
 (id,operation_id,user_id,coin_type,bucket,delta_coins,leg_order,created_at)
VALUES
 ('d0200000-0000-4000-8000-000000000005','d0200000-0000-4000-8000-000000000003','d0200000-0000-4000-8000-000000000001','Paid','Available',2000,1,'2026-01-01T00:01Z'),
 ('d0200000-0000-4000-8000-00000000000c','d0200000-0000-4000-8000-00000000000b','d0200000-0000-4000-8000-000000000001','Paid','Available',-2000,1,'2026-01-02T00:02Z'),
 ('d0200000-0000-4000-8000-00000000000d','d0200000-0000-4000-8000-00000000000b','d0200000-0000-4000-8000-000000000001','Paid','Reserved',2000,2,'2026-01-02T00:02Z'),
 ('d0200000-0000-4000-8000-000000000011','d0200000-0000-4000-8000-000000000010','d0200000-0000-4000-8000-000000000001','Paid','Reserved',-2000,1,'2026-01-02T00:04Z'),
 ('d0300000-0000-4000-8000-000000000005','d0300000-0000-4000-8000-000000000003','d0300000-0000-4000-8000-000000000001','Paid','Available',3000,1,'2026-01-01T00:01Z'),
 ('d0300000-0000-4000-8000-00000000000c','d0300000-0000-4000-8000-00000000000b','d0300000-0000-4000-8000-000000000001','Paid','Available',-3000,1,'2026-01-02T00:02Z'),
 ('d0300000-0000-4000-8000-00000000000d','d0300000-0000-4000-8000-00000000000b','d0300000-0000-4000-8000-000000000001','Paid','Reserved',3000,2,'2026-01-02T00:02Z'),
 ('d0300000-0000-4000-8000-000000000011','d0300000-0000-4000-8000-000000000010','d0300000-0000-4000-8000-000000000001','Paid','Reserved',-3000,1,'2026-01-02T00:04Z'),
 ('d0300000-0000-4000-8000-000000000012','d0300000-0000-4000-8000-000000000010','d0300000-0000-4000-8000-000000000001','Paid','Available',3000,2,'2026-01-02T00:04Z');

INSERT INTO billing.provider_transactions
 (id,provider,provider_transaction_id,payload_fingerprint,amount_vnd,direction,payment_code,account_number_last4,reference_code,matched_order_id,occurred_at,verified_at,processing_status)
VALUES
 ('d0200000-0000-4000-8000-000000000007','SePay','D01-COMPLETE-PROVIDER',repeat('3',64),20000,'Incoming','D01-COMPLETE','1111','D01-COMPLETE-REF','d0200000-0000-4000-8000-000000000002','2026-01-01T00:00Z','2026-01-01T00:01Z','Fulfilled'),
 ('d0300000-0000-4000-8000-000000000007','SePay','D01-RELEASE-PROVIDER',repeat('4',64),30000,'Incoming','D01-RELEASE','2222','D01-RELEASE-REF','d0300000-0000-4000-8000-000000000002','2026-01-01T00:00Z','2026-01-01T00:01Z','Fulfilled');

INSERT INTO billing.webhook_deliveries
 (id,provider_transaction_id,received_at,auth_verified,payload_reference,delivery_reference,processing_result)
VALUES
 ('d0200000-0000-4000-8000-000000000008','d0200000-0000-4000-8000-000000000007','2026-01-01T00:01Z',true,repeat('5',64),'D01-COMPLETE-DELIVERY','Accepted'),
 ('d0300000-0000-4000-8000-000000000008','d0300000-0000-4000-8000-000000000007','2026-01-01T00:01Z',true,repeat('6',64),'D01-RELEASE-DELIVERY','Accepted');

INSERT INTO billing.refund_requests
 (id,payment_order_id,user_id,payment_order_type,reason,original_amount_vnd,fee_amount_vnd,net_refund_vnd,required_recovery_coins,status,idempotency_key,request_fingerprint,request_fingerprint_version,requested_at,reviewed_by,reviewed_at,approved_by,approved_at,completed_by,completed_at,decision_reason,version)
VALUES
 ('d0200000-0000-4000-8000-000000000009','d0200000-0000-4000-8000-000000000002','d0200000-0000-4000-8000-000000000001','TopUp','D01 completed',20000,1000,19000,2000,'Completed','d01-complete-refund',repeat('7',64),'v1','2026-01-02Z','d0200000-0000-4000-8000-000000000001','2026-01-02T00:01Z','d0200000-0000-4000-8000-000000000001','2026-01-02T00:02Z','d0200000-0000-4000-8000-000000000001','2026-01-02T00:04Z','Completed payout',4),
 ('d0300000-0000-4000-8000-000000000009','d0300000-0000-4000-8000-000000000002','d0300000-0000-4000-8000-000000000001','TopUp','D01 released',30000,1500,28500,3000,'ReservationReleased','d01-release-refund',repeat('8',64),'v1','2026-01-02Z','d0300000-0000-4000-8000-000000000001','2026-01-02T00:01Z','d0300000-0000-4000-8000-000000000001','2026-01-02T00:02Z','d0300000-0000-4000-8000-000000000001','2026-01-02T00:04Z','Released reservation',4);

INSERT INTO billing.refund_recipients
 (id,refund_request_id,bank_code,account_number_encrypted,account_holder_encrypted,account_number_last4,account_number_masked,account_holder_masked,encryption_key_version,created_at)
VALUES
 ('d0200000-0000-4000-8000-00000000000a','d0200000-0000-4000-8000-000000000009','D01BANK',decode('112233','hex'),decode('445566','hex'),'1111','******1111','D01 COMPLETE','v1','2026-01-02Z'),
 ('d0300000-0000-4000-8000-00000000000a','d0300000-0000-4000-8000-000000000009','D01BANK',decode('778899','hex'),decode('aabbcc','hex'),'2222','******2222','D01 RELEASE','v1','2026-01-02Z');

INSERT INTO wallet.coin_reservations
 (id,refund_request_id,user_id,paid_coin_lot_id,wallet_operation_id,approved_by,idempotency_key,request_fingerprint,amount_coins,approval_result_status,approval_result_version,approval_result_reviewed_by,approval_result_reviewed_at,approval_result_decision_note,status,created_at,resolved_at)
VALUES
 ('d0200000-0000-4000-8000-00000000000e','d0200000-0000-4000-8000-000000000009','d0200000-0000-4000-8000-000000000001','d0200000-0000-4000-8000-000000000004','d0200000-0000-4000-8000-00000000000b','d0200000-0000-4000-8000-000000000001','d01-complete-reservation',repeat('9',64),2000,'RecoveryReserved',3,'d0200000-0000-4000-8000-000000000001','2026-01-02T00:01Z','Reserved original lot','Consumed','2026-01-02T00:02Z','2026-01-02T00:04Z'),
 ('d0300000-0000-4000-8000-00000000000e','d0300000-0000-4000-8000-000000000009','d0300000-0000-4000-8000-000000000001','d0300000-0000-4000-8000-000000000004','d0300000-0000-4000-8000-00000000000b','d0300000-0000-4000-8000-000000000001','d01-release-reservation',repeat('a',64),3000,'RecoveryReserved',3,'d0300000-0000-4000-8000-000000000001','2026-01-02T00:01Z','Reserved original lot','Released','2026-01-02T00:02Z','2026-01-02T00:04Z');

INSERT INTO billing.refund_transfer_records
 (id,refund_request_id,user_id,bank_reference,amount_vnd,evidence_reference,status,recorded_by,recorded_at,idempotency_key,request_fingerprint,verified_by,verified_at,confirmation_note,verification_idempotency_key,verification_fingerprint,resolution_operation_id)
VALUES
 ('d0200000-0000-4000-8000-00000000000f','d0200000-0000-4000-8000-000000000009','d0200000-0000-4000-8000-000000000001','D01-COMPLETE-BANK',19000,'D01-COMPLETE-EVIDENCE','VerifiedSucceeded','d0200000-0000-4000-8000-000000000001','2026-01-02T00:03Z','d01-complete-record',repeat('b',64),'d0200000-0000-4000-8000-000000000001','2026-01-02T00:04Z','Verified completed transfer','d01-complete-verify',repeat('c',64),'d0200000-0000-4000-8000-000000000010'),
 ('d0300000-0000-4000-8000-00000000000f','d0300000-0000-4000-8000-000000000009','d0300000-0000-4000-8000-000000000001','D01-RELEASE-BANK',28500,'D01-RELEASE-EVIDENCE','VerifiedFailed','d0300000-0000-4000-8000-000000000001','2026-01-02T00:03Z','d01-release-record',repeat('d',64),'d0300000-0000-4000-8000-000000000001','2026-01-02T00:04Z','Verified failed transfer','d01-release-verify',repeat('e',64),'d0300000-0000-4000-8000-000000000010');

INSERT INTO infrastructure.outbox_messages
 (id,event_type,payload,occurred_at,available_at,status,attempt_count)
VALUES
 ('d0200000-0000-4000-8000-000000000013','Billing.RefundCompleted.v1','{"refundRequestId":"d0200000-0000-4000-8000-000000000009","userId":"d0200000-0000-4000-8000-000000000001","paymentOrderId":"d0200000-0000-4000-8000-000000000002","paidCoinLotId":"d0200000-0000-4000-8000-000000000004","transferRecordId":"d0200000-0000-4000-8000-00000000000f","walletOperationId":"d0200000-0000-4000-8000-000000000010","refundedCoins":2000,"netRefundVnd":19000,"completedAt":"2026-01-02T00:04:00+00:00"}'::jsonb,'2026-01-02T00:04Z','2026-01-02T00:04Z','Pending',0),
 ('d0300000-0000-4000-8000-000000000013','Billing.RefundReservationReleased.v1','{"refundRequestId":"d0300000-0000-4000-8000-000000000009","userId":"d0300000-0000-4000-8000-000000000001","paymentOrderId":"d0300000-0000-4000-8000-000000000002","paidCoinLotId":"d0300000-0000-4000-8000-000000000004","transferRecordId":"d0300000-0000-4000-8000-00000000000f","walletOperationId":"d0300000-0000-4000-8000-000000000010","releasedCoins":3000,"releasedAt":"2026-01-02T00:04:00+00:00"}'::jsonb,'2026-01-02T00:04Z','2026-01-02T00:04Z','Pending',0);

COMMIT;
