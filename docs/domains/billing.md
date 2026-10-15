# Billing Module

Own PaymentOrders, SePay transaction ingestion, webhook deliveries, RefundRequests, protected bank recipients, manual transfer records. Provider payment verification and domain fulfillment are separate statuses. Orders types TopUp/VipPurchase; VND integer snapshots. TopUp valid range 10,000–2,000,000 VND, step 1,000; Paid Coins = VND/10.

TopUp PaymentOrder timestamps are server-owned. The default TTL is 30 minutes and is configurable through `Billing:TopUpOrderTtlMinutes` with fail-fast positive-value validation. PostgreSQL time is sampled only after entering the idempotency critical section. Processing a Pending order at `serverNow >= expiresAt` persists `Expired` and `expiredAt`; the transition is monotonic and is never reversed by clock skew. Expiry does not release `(User,TopUp,Idempotency-Key)`: same key and canonical amount replays the original order, including an expired order; a different amount conflicts; a new operation requires a new key.

B01 limits TopUp creation to 10 requests/minute/authenticated User and owns only order creation, validation, idempotency, expiry and persistence integrity. B02 owns authenticated SePay delivery ingestion and durable provider-event deduplication. Checkout/QR generation, order matching, late-payment policy and wallet credit remain **Verification Required** work for later Billing tasks.

## Payment
Unique order code and provider transaction identity. B02 reads the exact raw JSON body, verifies `X-SePay-Signature` as HMAC-SHA256 over `{X-SePay-Timestamp}.{rawBody}`, rejects timestamps outside the configurable five-minute default window, and checks the configured receiving account. The endpoint is disabled until a secret and receiving account are configured and is limited by remote address (120 requests/minute by default). Authenticated incoming events are normalized into `provider_transactions`; every accepted, duplicate or reconciliation-required delivery is recorded in `webhook_deliveries` in the same PostgreSQL transaction. `(provider, provider_transaction_id)` is the financial deduplication key and a same-ID/different-payload event is fail-closed into reconciliation. Raw payloads, secrets and full account numbers are not persisted.

B02 does not match or fulfill a PaymentOrder and never writes Wallet, PaidCoinLot, ledger or outbox state. Wrong direction/account or a missing payment code is retained as reconciliation evidence and acknowledged without credit. Invalid signature, stale timestamp and malformed input are rejected before persistence. Exact amount, expiry/late-payment policy, order matching and exactly-once fulfillment belong to B03. Frontend redirect or browser status is never payment proof. The documented signature/payload/acknowledgement behavior is covered by official-contract fixtures and automated tests, but an actual project-owned SePay Test-mode delivery trace remains **Verification Required** before production enablement.

## Refund rules
User requests only full original TopUp ≤7 days after verified paidAt, fee 5%, one request per TopUp even if Rejected. Collect bank recipient on request; no reservation until Admin approve. At approve atomically validate full original PaidCoinLot is available (`spent=refunded=reserved=0`), then reserve all. No Bonus/other lot substitution. Admin performs bank transfer externally, records reference/evidence. `NeedsReconciliation` retains reserve; confirmed successful transfer -> Completed and Wallet refund debit; confirmed failed -> Released. No automatic bank payout.

## Privacy
Encrypt recipient account number/holder, track key versions, mask in list/log/audit; restrict Admin visibility. Legal/privacy/retention validation before production.

## Acceptance
B02 acceptance is limited to: invalid authentication creates no financial evidence, duplicate provider identity creates no duplicate provider transaction, conflicting payloads and invalid financial direction/account are retained for reconciliation, and concurrent duplicate delivery converges on one provider transaction. Successful TopUp fulfillment and exactly-once Paid lot/Wallet credit are capability-level criteria owned by B03, not B02.
