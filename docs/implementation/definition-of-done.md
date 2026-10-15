# Definition of Done

A task is Done only if:
- Correct prerequisite tasks and relevant AGENTS/domain/API/ERD/UI documents were read; no undocumented scope expansion.
- Domain ownership and architecture dependency tests pass.
- DTOs/authorization/validation/errors/version and idempotency match relevant P0 contracts.
- If schema changed, PostgreSQL migration/index/FK/CHECK tests pass; no destructive data loss.
- Financial and cross-module workflow transactions are atomic, with append-only ledger, outbox/inbox and documented lock order.
- Unit + PostgreSQL integration/race tests for financial/security risks pass, and logs are redacted.
- Frontend renders Loading/Empty/Error/Pending/Success and relevant responsive/accessibility behavior.
- New feature documented, reported as tested with exact commands and results. Unrun provider verification remains marked pending.

## Capability-specific DoD
| Capability | Required proof |
|---|---|
| Wallet | Sum lots and ledger agrees, no negative balance, `spent` distinct from `refunded` |
| B01 TopUp order slice | PaymentOrder create contract, amount constraints, permanent idempotency, monotonic persisted expiry, authorization/CSRF/rate limit, PostgreSQL contention and upgrade migration evidence |
| TopUp capability (B02/B03) | Verify provider event, correct amount/order, duplicate webhook same grant and exactly-once wallet credit; remains incomplete while SePay is Verification Required |
| VIP | Concurrent extensions exact 720h each; old order price unchanged |
| Unlock | Bonus-first FIFO, quote 5 min, unique entitlement, no double debit |
| Refund | Single request/order; original lot full, reserve only after Admin approval; manual bank evidence, unknown state holds coins |
| Missions | 300/day cap, DeniedDailyCap, subject dedup, correct event-date and late-worker recovery |
| Reading | 90%+15s backend validation, absolute 30-minute sessions and protected manifest checks |
| Publishing | Cannot publish invalid Comic/Chapter, no hide last chapter, slug locked after first publish |
| Release | SePay and Cloudinary VERIFIED on actual accounts, backup restore and legal/privacy gates reviewed |

## Required Codex handoff
Report files changed, migration ID, tests run/pass/fail, unresolved assumptions, screenshots or traces for UI/provider work, and next unlocked dependent task. Never claim test success without execution.
