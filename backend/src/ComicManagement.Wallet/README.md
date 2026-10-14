# Wallet module

W01 owns the Wallet balance aggregate, Paid Coin lots and immutable ledger operations/entries.
Balances and coin quantities use `long`; domain guards and PostgreSQL constraints prevent
negative balances, over-consumed lots and arithmetic overflow. Paid lot accounting keeps
`spent`, `refunded` and `reserved` as distinct counters.

Authenticated read APIs expose only the current user's balance and cursor-paged ledger. A zero
Wallet row is created idempotently on first Wallet read so existing Identity users can adopt the
module without changing the Identity sign-in transaction.

W01 does not grant or spend Coins. TopUp fulfillment, Bonus rewards, Bonus-first/Paid-FIFO spend,
refund reservations and Reading unlock entitlement remain in their dependent tasks. Wallet
mutation workflows must lock the Wallet row first, then Paid lots in `(created_at,id)` order and
write balances, lots, ledger, allocations/entitlements and outbox in one PostgreSQL transaction.
