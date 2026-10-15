# Billing Module

Own PaymentOrders, SePay transaction ingestion, webhook deliveries, RefundRequests, protected bank recipients, manual transfer records. Provider payment verification and domain fulfillment are separate statuses. Orders types TopUp/VipPurchase; VND integer snapshots. TopUp valid range 10,000–2,000,000 VND, step 1,000; Paid Coins = VND/10.

TopUp PaymentOrder timestamps are server-owned. The default TTL is 30 minutes and is configurable through `Billing:TopUpOrderTtlMinutes` with fail-fast positive-value validation. PostgreSQL time is sampled only after entering the idempotency critical section. Processing a Pending order at `serverNow >= expiresAt` persists `Expired` and `expiredAt`; the transition is monotonic and is never reversed by clock skew. Expiry does not release `(User,TopUp,Idempotency-Key)`: same key and canonical amount replays the original order, including an expired order; a different amount conflicts; a new operation requires a new key.

B01 limits TopUp creation to 10 requests/minute/authenticated User and owns only order creation, validation, idempotency, expiry and persistence integrity. SePay payload/QR, provider event identity, webhook verification, late-payment policy and wallet credit remain **Verification Required** work for B02/B03.

## Payment
Unique order code and verified provider transaction identity. Read raw body and verify SePay authentication according to **tested** adapter contract, check transaction direction, target bank account, payment code, exact amount and expiry state before automatic fulfillment. Duplicate delivery or different delivery IDs referring to same transaction must never fulfill twice. Late/mismatch/unmatched transactions enter reconciliation with evidence; do not silently discard received money. Frontend redirect or browser status is not payment proof.

## Refund rules
User requests only full original TopUp ≤7 days after verified paidAt, fee 5%, one request per TopUp even if Rejected. Collect bank recipient on request; no reservation until Admin approve. At approve atomically validate full original PaidCoinLot is available (`spent=refunded=reserved=0`), then reserve all. No Bonus/other lot substitution. Admin performs bank transfer externally, records reference/evidence. `NeedsReconciliation` retains reserve; confirmed successful transfer -> Completed and Wallet refund debit; confirmed failed -> Released. No automatic bank payout.

## Privacy
Encrypt recipient account number/holder, track key versions, mask in list/log/audit; restrict Admin visibility. Legal/privacy/retention validation before production.

## Acceptance
Invalid/duplicate IPNs do not overcredit, late/unmatched IPN can be reconciled, successful TopUp exactly once creates Paid lot, rejected refund cannot be requested again, no approval with spent coins, no double-transfer-confirmed Completed.
