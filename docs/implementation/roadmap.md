# Ordered Capability Roadmap

Tasks are small vertical slices with tests. Parallelism allowed only where dependencies permit. Detailed edges in `task-dependencies.md`.

| Phase | Deliverable | Exit |
|---|---|---|
| 00 | .NET 10 solution (13 projects), React shell, EF/Postgres, CI, Shared, Outbox skeleton, architecture tests | Clean build + migration test |
| 01 | Google OAuth, cookie/CSRF, User/Admin, editable displayName | Auth/role tests |
| 02 | Catalog core + minimal Admin editor + Cloudinary upload | Draft/publish/public invariants |
| 03 | Free Reader, sessions, progress, follows/Library | Reading and qualification tests |
| 04 | Wallet operations/ledger/lots/BONUS core | Balance/FIFO/reconciliation tests |
| 05 | PaymentOrder, SePay adapter + test integration, TopUp fulfillment | Idempotent verified credit |
| 06 | VIP checkout/renewal/entitlement | Concurrent renewals tests |
| 07 | Unlock quote + 5-minute price revision + spend/entitlement | Bonus-first Paid FIFO, concurrency tests |
| 08 | Missions definitions/revisions/receipts/reward worker | Event-time cycle + cap 300 |
| 09 | Refund request/recipient/approve/reserve/manual completion | One request/order + exact lot verification |
| 10 | Ratings/Comments/Moderation | Owner/auth/audit tests |
| 11 | Full Admin Content/Featured/Users/Payments/VIP/Missions/Dashboard/Audit | Admin E2E and reporting consistency |
| 12 | Security and recovery, PostgreSQL integration, E2E, Cloudinary/SePay POC | Acceptance evidence |
| 13 | Docker/Caddy staging/production preparation | Backup/restore, go-live gates |

Do not defer all testing until phase 12. Do not launch actual paid content before media access gate; do not take real payments before SePay and legal/reconciliation gates.
