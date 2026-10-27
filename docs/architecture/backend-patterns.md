# Backend Patterns and Security

## Vertical slices
Controllers: route/auth/DTO mapping; application handler: validation/use-case; module domain: invariants; module ports: repository abstractions; Persistence: EF configurations/queries; Workflows: cross-domain orchestration. Avoid redundant generic repository layers and premature event buses.

## Financial consistency
`wallet_operations` group immutable `wallet_entries` by operation and ledger leg. Append rather than edit entries. PaidCoinLot has `granted_coins`, `spent_coins`, `refunded_coins`, `reserved_coins`. Every mutation of balance, lots, allocations/reservations and ledger is atomic. Operation-specific uniqueness + client idempotency keys + provider-transaction uniqueness all matter independently.

## Outbox
Transactional event writes; worker claims pending events via `FOR UPDATE SKIP LOCKED` (bounded batches), records attempts/next retry, and consumer processed-message IDs in same transaction as effects. Poison messages surfaced to operator, never silently discarded. Source action occurredAt is backend-authoritative and retained for Missions.

## Security
OAuth server-side callback with state/nonce/PKCE as applicable, enforce issuer/audience/subject and anti-login-CSRF; cookie HttpOnly Secure and CSRF defense on unsafe endpoints; production behind same-origin HTTPS/Caddy. Apply authorization per resource and roles per endpoint. Persist ASP.NET Data Protection keys across deploys. Encrypt bank recipient account data, redact audit/logs, avoid logging tokens or raw provider secrets. Rate-limit auth, order creation, comments, progress and webhook.

## Publishing
Chapter Published under Draft Comic is not public. Publish Comic requires >=1 Published Chapter with >=1 valid page. Hide last Published valid Chapter of a Published Comic is refused until parent hidden. Slug permanently immutable after first publish. Reject changes that invalidate Published chapter/page invariants.

## Open issues
Cloudinary time-bounded protected delivery mechanism and SePay account-specific response/payload are Verification Required. No placeholder security pretending paid media is protected.
