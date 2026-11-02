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
5. Publish API, web, migrator and operations images through a protected release
   workflow. Generate a release manifest containing immutable OCI digests,
   Git SHA, migration identity and timestamp; deploy and roll back by digest.
6. Produce age-encrypted backups with an independently signed provenance
   manifest. Verify provenance before decryption or destructive SQL, restore on
   a separate internal Compose project/network, and clean its containers,
   network and volume on both success and failure.
7. Strengthen generic recovery checks for provider/payment, Wallet/ledger/lots,
   refunds/reservations, outbox and audit state, while retaining separate
   fixture-specific assertions as deterministic CI evidence.
8. Add automated tests for permission, provenance, isolation, two-distinct-image
   rollback, health and deployment invariants; run solution, PostgreSQL,
   frontend, Compose, smoke and restore gates.

## Migration and transaction impact

No EF model/schema change is planned. The deploy migrator is a single controlled
job and the API waits for its successful completion. Existing financial
transaction, idempotency, ledger and outbox semantics remain unchanged.

The only new persisted metadata is external deployment/backup metadata (release
manifest and signed backup manifest); no application schema or migration is
added.

## Verification blockers retained

- SePay project Test-mode delivery/replay/reconciliation traces
- Cloudinary paid/VIP browser/account protection drill and exposure window
- Production Google redirect/cookie drill through the actual domain
- Legal/privacy review for refunds and manual payout process validation
- VPS capacity/load evidence and an operator-selected encrypted offsite target
