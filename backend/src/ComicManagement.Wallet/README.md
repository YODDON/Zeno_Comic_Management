# Wallet module

W01 owns the Wallet balance aggregate, Paid Coin lots and immutable ledger operations/entries.
Balances and coin quantities use `long`; domain guards and PostgreSQL constraints prevent
negative balances, over-consumed lots and arithmetic overflow. Paid lot accounting keeps
`spent`, `refunded` and `reserved` as distinct counters.

Authenticated read APIs expose only the current user's balance and cursor-paged ledger. A zero
Wallet row is created idempotently on first Wallet read so existing Identity users can adopt the
module without changing the Identity sign-in transaction.

W01 does not grant Coins. TopUp fulfillment, Bonus rewards, refund reservations and Reading unlock
entitlement remain in their dependent tasks.

W02 provides an internal transaction-neutral spending capability. Wallet-owned policy validates
the command, consumes Bonus before Paid and allocates Paid FIFO by `(created_at,id)`. Persistence
locks the Wallet row first and then the Paid lots, checks balances against both lot and ledger
equations, and stages balances, lots, immutable ledger and allocations in the caller's active
PostgreSQL transaction. Allocation ownership is also enforced by the database.

W02 intentionally registers no standalone command/store that can commit a debit by itself. A
downstream financial workflow (for example U02) must open the transaction, invoke the internal
engine, stage its business effect and outbox message, then save and commit all effects together.
The business source and scoped idempotency key identify a replay; payload/source mismatches are
conflicts and never debit again.
