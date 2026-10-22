# Frozen MVP Decisions and Decision Status

All below are **approved product rules** from the design process. Other implementation choices may be proposed but not silently converted into product policy.

## Product
Google OAuth exclusively; User/Admin. No in-app Admin promotion. Admin can restore Hidden comments to Visible with audit, never restore deleted comment. Admin only manages upload/publish. Comics/Chapters Draft/Published/Hidden; series Ongoing/Completed/Hiatus. Chapter Published under Draft Comic not public; Comic publication requires >=1 valid Published Chapter; forbid hiding last while parent Published. Slug frozen forever at first publish. Never hard-delete Comic/Chapter with reading/payment history. Chapter label text separated from numeric `sort_order`.

## Payments & Wallet
SePay TopUp 10,000–2,000,000 VND per order in 1,000 VND steps. Exchange 1,000 VND = 100 Paid Coins. Bonus earned via Missions. Spend Bonus first then PaidCoinLots FIFO. Ledger append-only, Paid available/reserved distinct. Exact PaidCoinLot fields `granted_coins`, `spent_coins`, `refunded_coins`, `reserved_coins` (NOT `depleted_coins`). Unlock quote expires in 5 minutes bound to user/chapter/price revision. Unique persistent unlock per user/chapter, except free/VIP access is temporary eligibility rather than purchased unlock.

## VIP
Initial price 29,000 VND; Admin may update future orders, immutable price snapshot for existing orders. Duration exactly 720 hours added to max(existing expiry, successful backend fulfillment timestamp). VIP access to eligible chapters; 20% discount for other chapters' coin unlock, round up integer coins.

## Refund
Only full original Paid TopUp, request ≤7 days after verified paid time, fee 5%, one refund request per TopUp **even if rejected**. Bank details collected at request; Admin reviews and only then reserves *all* granted coins from exact original lot if completely unspent/unrefunded/unreserved. Reservation, Wallet/lot counters, immutable ledger, redacted audit and a recipient-free recovery-reserved outbox event commit atomically. Approval idempotency replays its immutable original result independently of later payout lifecycle state. No exception payout for spent coins; do not use Bonus or other Paid lots. Manual external bank transfer, success evidence before Completed, unknown result requires reconciliation and held reservation; confirmed failed transfer permits release. Legal/privacy review before launch.

## Missions
Actions DailyLogin, ChapterReadQualified, ComicFollowed, ChapterUnlocked, WalletTopUpCompleted; Fixed/Daily/Weekly/Event; weekly Monday 00:00 to next Monday 00:00 Asia/Ho_Chi_Minh. Unique chapter/follow/order subject dedup by mission policy. Auto Bonus rewards, 300 Bonus Coins/user/Vietnam local day fixed read-only. If reward exceeds cap, deny **whole reward** (`DeniedDailyCap`); no partial or carryover. Determine period and reward cap date from authoritative action timestamp, not worker processing time. Worker delay never automatically forfeits valid reward; retain retry/operations escalation. Version mission policy for in-flight progress.

## Reading/Interaction/UI
Vertical continuous-scroll image reader, theme follows user Dark/Light; mobile toolbar auto-hides on downward scroll. Read qualified only at >=90% valid progress AND >=15 seconds valid active time within an absolute non-sliding 30-minute session, backend verified. Rating 1–5 one/user/comic, editable. Comments comic/chapter, max one reply level, Admin hide/delete/restore Hidden. Home manual Featured max five Published comics, draggable order; Catalog, Detail load 30 chapters at a time, Reader, Library Continue/Follow, editable display name, Wallet, VIP, Missions, Refunds. Admin always Light, advanced dashboard and dedicated editors.

## Baseline architecture
.NET 10 + EF Core 10 proposed technical baseline; Modular Monolith (13 production projects), PostgreSQL, React/TypeScript/Vite/Tailwind/shadcn, Caddy/Docker Compose on single VPS, Cloudinary and SePay. Protected media and provider payload/response remain **Verification Required**.

## Not approved/needs verification
Specific Cloudinary expiry/token method, SePay checkout-vs-QR and exact IPN response/payload, infrastructure sizing, actual banking transfer confirmation mechanism, legal suitability of 5% fee/7-day policy, detailed session TTLs, full OpenAPI schemas and tested migrations. Mark unresolved choices honestly; do not guess values.
