# Task Execution Plan Template

## Scope
State exact capability, actor, dependent domains and non-goals. List prerequisite task IDs from `docs/implementation/task-dependencies.md`.

## Read only required specs
Root/backend/frontend AGENTS, relevant domain, API, database and UI docs. Flag ambiguities before coding.

## Implementation steps
1. Propose endpoint DTO + error codes, auth and idempotency behavior.
2. Define DB ownership, migration, indexes/uniques and transaction/lock order.
3. Implement domain behavior + ports + persistence + workflow + thin controller.
4. Add frontend query/mutation and explicit UI states if in scope.
5. Add unit/integration/architecture/concurrency/E2E tests appropriate to risk.
6. Update relevant docs and note provider verification still outstanding.

## DoD report
Files changed; migration; tests run and results; outstanding risks; acceptance cases; no unverified claims. Do not start dependent work on a failing foundation.
