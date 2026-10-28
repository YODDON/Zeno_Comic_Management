# Administration Module

Admin UI always Light. Navigation Dashboard; Content (Comics, Chapters, Genres, Authors, Translation Groups, Featured); Users; Finance (Payments, Ledger, Refunds); Benefits (VIP, Missions); Moderation; Settings; Audit.

Advanced dashboard: verified VND payments, TopUp/VIP revenues, refunds, coins sourced Paid vs Bonus, readers/missions activity. Do not treat Bonus Coins as VND revenues or Paid Coins nominal value as revenue. Provide exact metric definitions/date range/timezone.

A04 uses `Asia/Ho_Chi_Minh` calendar dates and an inclusive 30-day default, with a maximum of 366 days. Each request converts `[from 00:00,to+1 00:00)` to UTC and reads all totals/series from one PostgreSQL RepeatableRead snapshot. TopUp and VIP gross use fulfilled orders and `verified_paid_at`; completed refunds remain separate original/fee/net metrics. Paid issuance comes from fulfilled TopUp lots, Bonus issuance from Granted Mission rewards, reading comes from one-time `ChapterReadQualified` outbox events, and Mission activity separates receipts, Granted decisions and DeniedDailyCap decisions. Audit lists use the same date semantics, deterministic `(occurred_at DESC,id DESC)` ordering, one snapshot for count/items, and expose only `redacted_changes`. Audit changes pass through one recursive case-insensitive policy before persistence and again on exposure so nested/array banking fields, recipient values, ciphertext, credentials and raw provider/financial payloads cannot leak; malformed legacy JSON fails closed without logging the stored value.

## Permissions
All Admin endpoints have server role checks; first Admin provision out-of-band, no Admin grant/revoke UI. Users can be disabled according to audit policy, never directly edit Wallet balances. Moderation Hide/Delete/Restore Hidden comments with audit. Restore Deleted disallowed.

## Settings
Reading 90%/15s thresholds may be exposed as typed settings with session policy snapshot if product later enables editing; fixed cap 300 Bonus/day is read-only, no Admin override. VIP 720h/20% fixed MVP, price editable for *new orders* and snapshotted. SePay/Cloudinary secrets never in UI.

A03 implements Benefits and Users without adding undocumented settings: Mission revision/activation UI reuses the M01/M02 APIs and exposes a read-only, paged reward-decision report (`Granted|DeniedDailyCap`); VIP edits only future-order price; Users supports versioned enable/disable with read-only Wallet/membership/access/payment context. Admin query caches include the authenticated Admin ID. The Settings screen is intentionally read-only for Reading thresholds, Mission cap/timezone and provider verification state; no generic typed-setting write API is introduced.

## Featured
Up to five Published Comics, drag-drop reorder and keyboard alternative; save atomic with configuration version; public result excludes Comic if later Hidden. Audit admin mutating commands; immutable audit entries redacted of sensitive data.

## Finance workspace
A02 exposes read-only PaymentOrder/provider evidence and immutable Wallet ledger projections plus the RF02/RF03 refund workspace. Finance reads are Admin-only, no-store and paged; refund detail is a repeatable-read view of the request, masked recipient, exact original PaidCoinLot, reservation, transfer evidence and redacted audit history. It never returns recipient plaintext/ciphertext or request fingerprints. Public finance filter/status values are closed string enums in OpenAPI and invalid/numeric values fail validation. Payment reconciliation display uses deterministic `(verified_at DESC,id DESC)` ordering. UI finance caches are scoped to the authenticated Admin ID and refund evidence form state resets when the selected refund changes. UI actions call the existing audited RF02/RF03 commands and must distinguish recording reported transfer evidence from independently confirming Complete/Release. No direct Wallet adjustment is available.
