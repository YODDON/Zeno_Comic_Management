# D01 Docker, deploy and restore execution plan

## Scope

Package the existing modular monolith for a single-VPS deployment: one ASP.NET
Core API/outbox process, one PostgreSQL database, React static assets behind
Caddy, and a controlled one-shot EF migration artifact. Add health checks,
configuration validation, backup/restore tooling and deployment evidence.

Prerequisites: F06, Q01, Q02 and Q03 are merged in `dev`.

Non-goals: no provider business changes, no financial mutations, no schema
migration, no live SePay/Cloudinary credential provisioning, no VPS sizing and
no claim that provider, legal/privacy or production OAuth gates are verified.

## Required specifications

- Root/backend/frontend `AGENTS.md`
- `docs/implementation/{roadmap,task-dependencies,definition-of-done}.md`
- `docs/operations/{deployment,backup-and-recovery,verification-gates}.md`
- `docs/database/{migrations,physical-erd,financial-invariants}.md`
- `docs/architecture/{dependency-rules,backend-patterns}.md`
- `docs/api/conventions.md`

## Implementation steps

1. Add multi-stage API and migration-bundle images plus a Caddy/static frontend
   image with same-origin API routing and security headers.
2. Add Compose orchestration with private PostgreSQL, a one-shot migration job,
   persistent Data Protection/Caddy/database volumes, health gates and explicit
   trusted proxy networking.
3. Add API liveness and database/migration-aware readiness without automatic
   application startup migration.
4. Add a non-secret environment template and fail-fast deployment validation.
5. Add backup, checksum, isolated restore verification and rollback/runbook
   documentation. Offsite storage remains operator-selected.
6. Add automated tests for health behavior and deployment artifact invariants;
   run solution, PostgreSQL, frontend, Compose, smoke and restore gates.

## Migration and transaction impact

No EF model/schema change is planned. The deploy migrator is a single controlled
job and the API waits for its successful completion. Existing financial
transaction, idempotency, ledger and outbox semantics remain unchanged.

## Verification blockers retained

- SePay project Test-mode delivery/replay/reconciliation traces
- Cloudinary paid/VIP browser/account protection drill and exposure window
- Production Google redirect/cookie drill through the actual domain
- Legal/privacy review for refunds and manual payout process validation
- VPS capacity/load evidence and an operator-selected encrypted offsite target
