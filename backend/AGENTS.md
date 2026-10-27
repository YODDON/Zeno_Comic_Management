# Backend Codex Rules

- Target .NET 10, ASP.NET Core Controllers with vertical slices per domain. `ComicManagement.Api` composes HTTP, DI and background hosting; do not put domain logic in controllers.
- **13 production projects**: Api, Workflows, Persistence, Shared, and nine modules: Identity, Catalog, Reading, Interaction, Billing, Wallet, Membership, Missions, Administration. Test projects are additional.
- Module projects depend only on Shared/business-neutral contracts; never directly on another module, Persistence or Api. Workflows orchestrate using public module contracts. Persistence implements ports and a single scoped `AppDbContext` with schema per module. One migration stream.
- Cross-domain workflows use one unit of work/transaction, with no repository making independent commits. Explicit lock order, idempotency, unique constraints, outbox and transactional ledger are mandatory on financial paths.
- Store UTC `timestamptz`; compute Mission day/week in `Asia/Ho_Chi_Minh`. Use integer `bigint` for VND/coins, no floating-point money.
- Cookie auth HttpOnly/Secure/SameSite, CSRF protection for mutations, authorization on every Admin endpoint, rate limits and structured logging with no secrets/bank account disclosure.
- Never infer SePay payment success from frontend; verify provider events. Do not issue Cloudinary protected-page URLs before authorization.
- For every feature: request/response and errors, authorization, validation, transaction, tests, migration, operational implications. Review `docs/architecture/backend-patterns.md`, `docs/api/contracts-p0.md`, `docs/database/physical-erd.md`.
