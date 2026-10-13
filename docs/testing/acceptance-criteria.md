# MVP Acceptance Criteria

## Identity/Catalog
Google OAuth only and no Admin promotion UI. Comic cannot publish without valid Published Chapter; Chapter can publish under Draft Comic but remains invisible; cannot hide/delete last valid published chapter of public Comic. Slug immutable once ever Published. Chapter labels don't determine sort, 30 per Load More.

## Reading/Interaction
Free guest reader; paid manifest denied without entitlement; VIP timebound and coin unlock durable while Published; 90% and 15 seconds both validated server-side, not client-declared, within an absolute non-sliding 30-minute session. Resume position safe under multiple tabs. Rating uniqueness, comments one reply level, Admin restore Hidden audited.

## Wallet/Billing/VIP
TopUp limits and 100 coins/1000 VND; one SePay financial transaction one grant, despite retries. Bonus-first, Paid FIFO and lot equation; no balance negative. VIP starts at backend fulfill and adds 720h per unique order; 20% ceil coin discount, order price snapshots.

## Missions/Refund
Daily cap 300 by local action time, overflow denied whole, no lost reward from worker delay, dedup by subject and revision. Refund in seven days, whole original lot, 5% fee, one request/order, reserve after Admin approval, transfer evidence and no double payout/credit. Privacy controls and audit.

## Operations
Schema migration tests, PostgreSQL integration, outbox crash/retry, backup restore and protected-media/provider proof of concept. Detailed cases `financial-concurrency.md`, `e2e-scenarios.md`.
