# Dependency and Ownership Rules

| Project | May reference |
|---|---|
| Shared | BCL and domain-neutral libraries only |
| Each business module | Shared; no other business module, Persistence or Api |
| Workflows | Business module public contracts, Shared; not Persistence |
| Persistence | Module ports/entities/configurations, Shared and EF Core |
| Api | Workflows, modules public contracts, Persistence for DI composition |

One `AppDbContext`, one migration stream, schemas per module. Infrastructure code references necessary modules for mapping but must not create backdoors letting business logic bypass module contracts. Database FKs may cross schemas because a single DB is used; avoid direct arbitrary cross-module queries in application logic.

### Ownership
- Reading owns `comic_follows`, reading progress, sessions, chapter unlock rights.
- Interaction owns ratings/comments.
- Billing owns orders, provider transactions/deliveries, refunds/bank evidence.
- Wallet owns coins, lots, reservation, ledger; it never owns the refund approval policy.
- Membership owns VIP plan/subscription/transactions.
- Missions owns definitions, revisions, action receipts, progress, rewards, daily counter.
- Administration owns audit/settings. Outbox/inbox are infrastructure.

### Transaction discipline
All use-case repositories share scoped unit of work, do not independently commit. Wallet-related workflows lock Wallet first, then lots in a deterministic order, then revalidate business aggregate state under lock; use unique keys and optimistic versions. Retry only safe transient failures while preserving idempotency. External provider calls are **outside** DB transactions; callbacks/evidence then transition state transactionally.
