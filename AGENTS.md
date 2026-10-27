# Codex Instructions — Comic Management

## Mission
Build and deploy a Vietnamese-language image-comic platform using **ASP.NET Core .NET 10 modular monolith**, **PostgreSQL**, **React/TypeScript**, Cloudinary and SePay. Implement only documented MVP features. This repository's documents are the approved product blueprint, **not evidence that providers or APIs have already been tested**.

## Before every task
1. Read `docs/implementation/roadmap.md`, `docs/implementation/task-dependencies.md` and the applicable domain document(s).
2. Backend tasks: read `backend/AGENTS.md`, `docs/architecture/dependency-rules.md`, `docs/api/conventions.md`. Database changes: read `docs/database/physical-erd.md` and `docs/database/financial-invariants.md`.
3. Frontend tasks: read `frontend/AGENTS.md`, relevant `docs/ui/*` and endpoint contracts.
4. Create an explicit plan for multi-step work following `.agent/PLANS.md`. Identify migration and tests, and list unresolved provider verification blockers.
5. Make the smallest coherent vertical slice, run relevant tests, report evidence. Never mark a verification gate passed without executing it.

## Hard rules
- **Only Google OAuth**; roles User/Admin. No email/password, self-promotion, or web Admin role management.
- **One backend deployable, one PostgreSQL database, one migration stream**; no microservices, Kafka, RabbitMQ, Redis or n8n for MVP.
- Financial writes use one PostgreSQL transaction, persistent idempotency, immutable ledger, outbox, and concurrency controls. **Bonus before Paid FIFO**; never let a wallet balance or PaidCoinLot go negative.
- Refund **only original whole TopUp**, within 7 days, 5% fee, at most one request per TopUp, manual Admin approval and payout; approve only if the **original PaidCoinLot** retains all granted coins. Never substitute other lots or Bonus.
- VIP starts at **29,000 VND/720h**, Admin can change future-order price; existing order price is snapshotted. VIP discounts other coin-locked chapters by 20%, **round UP**.
- Missions grant Bonus automatically, capped at **300 per user per Asia/Ho_Chi_Minh day**, no partial grant/carryover. Action-time governs the period even if worker runs late.
- Comic may be published only if it has >=1 valid Published Chapter; a Chapter may be Published while its Comic is Draft but not publicly accessible. Never hide the last valid Published Chapter while the Comic remains Published.
- Paid/VIP image delivery is **NOT VERIFIED** for Cloudinary; do not expose public paid-page URLs and do not claim access protection without a tested proof of concept.
- SePay payment method, webhook contract, event identity and reconciliation remain **Verification Required**. A frontend redirect does not confirm payment.

## Source of truth and changes
`docs/architecture/decisions.md` is the frozen product policy; domain docs own invariants; `docs/api/contracts-p0.md` owns P0 wire contracts; `docs/database/physical-erd.md` owns schema intent; UI docs own presentation. If documents conflict, stop, document the discrepancy and propose a narrowly scoped ADR; do not invent a rule. Detailed DTOs/index choices not yet fully validated remain implementation proposals until tested. Keep relevant docs synchronized when a decision is approved.

## Exit criteria
Follow `docs/implementation/definition-of-done.md`; pass compilation/tests/constraints and explicitly list pending Verification Required items. Never claim deployed or tested work based on generated files alone.
