# Financial Invariants and Ledger Accounting

## Equations (transaction invariant)
For each PaidCoinLot: `Available = Granted - Spent - Refunded - Reserved >= 0`. Aggregate: `Wallet.PaidAvailable = Σ Lot.Available`, `Wallet.PaidReserved = Σ Lot.Reserved`, `Wallet.BonusAvailable = Σ Bonus ledger deltas`. `PaidAvailable` EXCLUDES PaidReserved. Every transfer between buckets has ledger legs; no direct Admin balance adjustment.

## Example: unlock 80 Coins, Bonus=30, PaidLots [20,100]
BonusAvailable -30; PaidAvailable -50. Paid allocations: first lot 20, next lot 30. Paid lots' `spent_coins` increment by 20 and 30. `wallet_operations` source unique `ChapterUnlock`, `wallet_entries` Bonus Available -30 and Paid Available -50. In same transaction insert Reading.chapter_unlock and outbox ChapterUnlocked; unique `(user_id,chapter_id)`. The wallet must not be debited twice even if idempotency keys differ.

## Example: complete full 100,000 VND TopUp refund
Original Paid lot granted 10,000; before approval `spent=0`, `refunded=0`, `reserved=0`, lotAvailable=10,000. Approve: lot reserved+10,000; wallet PaidAvailable -10,000, PaidReserved +10,000; append two legs. Externally transfer 95,000 VND after fee 5,000. Verified success: lot reserved−10,000, refunded+10,000; wallet PaidReserved−10,000; append refund completion leg. Verified failed: lot reserved−10,000; wallet PaidReserved−10,000, PaidAvailable+10,000; append reversal legs. Ambiguous payout -> do nothing to reserve.

## Required invariants
- 1 fulfilled TopUp = 1 immutable grant of Paid lot; 1 provider transaction cannot fulfill 2 orders.
- 1 fulfilled VipPurchase = exactly 1 immutable `VipTransaction`, exactly 1 fulfilled provider transaction and exactly 1 VIP-activation outbox message whose identifiers and snapshots agree. `VipSubscription.Version` equals the latest consecutive per-user `SubscriptionVersion`, with no missing/intermediate version and a continuous `PreviousExpiry -> NewExpiry` chain. The complete historical artifact chain is validated fail-closed before renewal.
- A VIP success atomically updates PaymentOrder/provider state, extends the subscription, appends its versioned VIP transaction and writes one activation outbox row. Late, mismatched or integrity-drifted payment evidence creates no entitlement or success outbox. VIP orders never credit Paid Coins; no Bonus Coins refunds.
- Bonus before Paid FIFO and never consume reserved Paid.
- Refund whole original Paid lot or reject: never substitute a different lot; 7-day request window, 5% fee, one request/order.
- Complete manual refund only with external transfer evidence and reference; outcome ambiguity blocks automatic release/retry.
- Mission grants Bonus at most 300 per VN local event date, without partial grant; transactionally update daily counter and wallet.
- Reconciliation compares balances against ledger/lot components; discrepancy alerts operator, never silently repairs without audited procedure.

## Lock and idempotency
Predetermine lock order across workflows; start with Wallet row for all wallet mutations, then Paid lots in deterministic order, then recheck use-case states under lock. VIP renewal locks the provider identity, per-user subscription/advisory key, subscription row and PaymentOrder in the documented order; concurrent renewals serialize and allocate consecutive subscription versions. Use DB uniqueness for business entitlement/order, idempotency records for client request replay, provider transaction uniqueness for webhook. Retry safe transient DB failures using same semantic operation key.
