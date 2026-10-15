# Task IDs and Dependencies

| Task ID | Description | Depends on | Evidence |
|---|---|---|---|
| F01 | Production csproj boundaries and tests | None | Build, architecture tests |
| F02 | Shared error/clock/identity abstractions | F01 | Unit tests |
| F03 | EF DbContext, schemas, migrations | F01 | PostgreSQL migration test |
| F04 | Outbox/inbox and hosted-worker skeleton | F03 | Crash/retry integration |
| F05 | React shell, API client, theme | F01 | Build/UI smoke |
| F06 | CI/CD test pipeline | F01,F03,F05 | PR pipeline |
| I01 | Google OAuth and cookie session | F02,F03 | OAuth/session tests |
| I02 | CSRF + role authorization and Admin provisioning procedure | I01 | 401/403 tests |
| I03 | Profile displayName API/UI | I01,F05 | API/UI tests |
| C01 | Catalog taxonomy and comics schema | F03 | EF constraints |
| C02 | Chapter/page schema and Cloudinary upload adapter | C01 | upload/error tests |
| C03 | Publication workflow, slug lock | C02,I02 | invalid publish/hide tests |
| C04 | Public list/detail/chapter access-summary | C03 | 30-chapter pagination test |
| R01 | Access projection and manifest access guard | C04,I02 | deny paid/hidden |
| R02 | Sessions, progress, qualification 90%/15s with absolute 30-minute expiry | R01 | anti-cheat/race tests |
| R03 | Follow + Continue/Library UI | R02,F05 | read/follow E2E |
| W01 | Wallet, operations, entries, Paid lots | F03,I01 | ledger reconciliation |
| W02 | Atomic spending, allocations and deterministic locks | W01 | FIFO/race tests |
| B01 | PaymentOrder and idempotent create API | F03,I01 | amount/idempotency, monotonic expiry, per-user rate limit, PostgreSQL contention |
| B02 | Provider adapter and webhook transaction/delivery tables | B01 | SePay contract verification PENDING |
| B03 | TopUp provider verified fulfillment | B02,W01,F04 | exactly-once credit |
| V01 | VIP plan/order snapshots and renewal | B03,I01 | concurrent renewal tests |
| U01 | Five-minute quotes and access logic | R01,V01,W02 | quote/price tests |
| U02 | Atomic unlock with Reading entitlement | U01,F04 | duplicate/concurrent unlock tests |
| M01 | Definitions/revisions/period rules/action receipts | F03,F04 | VN timezone tests |
| M02 | ProgressActions/dedup and reward counter/wallet | M01,R02,R03,B03,U02,W01 | cap/retry tests |
| RF01 | Refund request, recipients and eligibility | B03,W01,I02 | one/order and 7d tests |
| RF02 | Admin approval/reserve original lot | RF01,W02 | under-lock eligibility/race tests |
| RF03 | Manual transfer evidence/Complete/Release | RF02 | confirmation/no-double test |
| IN01 | Comic Ratings | C04,I01 | uniqueness/tests |
| IN02 | Comments + Hide/Delete/Restore | C04,I02 | reply-depth/audit |
| A01 | Full Editors + Featured reorder | C03,F05 | keyboard/concurrency |
| A02 | Finance/Refund Admin UI | RF03,F05 | evidence flow tests |
| A03 | Mission/VIP/Users/Settings Admin UI | M02,V01,I02,F05 | version/audit tests |
| A04 | Advanced Dashboard and Audit | A02,A03,IN02 | metric semantics |
| Q01 | Full PostgreSQL race/recovery suite | U02,RF03,M02,V01 | passing evidence |
| Q02 | SePay/Cloudinary security proof of concept | B02,R01,C02 | provider verification evidence |
| Q03 | E2E/responsive/security | A01,A02,A03,IN02,Q01 | Playwright/security |
| D01 | Docker Compose, Caddy, deploy and restore | F06,Q01,Q02,Q03 | staging & release gates |

Notes: W01 can proceed in parallel with C/R after Identity; Cloudinary upload needed early in C02, but protection POC gates *paid* production later. F04 is shared infrastructure, not Missions-owned. Rating/Comments is independent path but must reach E2E before release.
