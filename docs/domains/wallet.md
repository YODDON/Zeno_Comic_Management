# Wallet Module

## Canonical invariants
Each User has Wallet (`paid_available`, `paid_reserved`, `bonus_available`, version); a TopUp fulfillment yields exactly one PaidCoinLot with **granted_coins, spent_coins, refunded_coins, reserved_coins**. `lotAvailable = granted - spent - refunded - reserved ≥ 0`; `Wallet.paid_available = Σ lotAvailable`; `Wallet.paid_reserved = Σ lot.reserved`. Bonus is separate; never refundable.

For ChapterUnlock consume Bonus first, then PaidCoinLots FIFO `(created_at ASC, id ASC)`; all lot allocation and debit legs are in the same transaction as Reading entitlement. WalletOperations group append-only WalletEntries with `(coin_type, bucket, delta_coins, leg_order)`; WalletAllocations associate spend with PaidCoinLot; CoinReservations associate exact refund to original lot.

## Refund accounting
Reserve: Paid available −N / Paid reserved +N; lot reserved +N. Transfer success: Paid reserved −N, lot reserved −N, `refunded_coins +N`. Verified failed payout: Paid reserved −N / Paid available +N; lot reserved −N. **Never change `spent_coins` on a refund**. One reservation/refund request; unique business operation keys.

## Quote
Unlock quote bound to authenticated User, Chapter and price/policy revision, expires in five minutes. It is an estimate, not held balance. Revalidate access, visibility, quote, current wallet/lot balance and pricing transactionally at commit. A retry with same idempotency key produces same result, different key cannot bypass unique User/Chapter unlock.

## Tests
Negative balance blocked at DB+domain; concurrent unlock/refund/mission reward; FIFO; Bonus first; compare ledger to balances and lots; retries; crash recovery; no irreversible mutation from quote only.
