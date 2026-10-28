# Admin Screen Specifications

Admin is Light-only, productivity-oriented sidebar with advanced Dashboard. All actions require backend Admin role and auditable commands.

## Content
Comic and Chapter lists provide server-side search, publication-status filtering and one-based pagination (30 items by default, maximum 100) plus dedicated full-page editors. The Comic editor owns title, immutable-once-published slug, cover identifier, metadata/taxonomy, default pricing/VIP flags and publication status. The Chapter editor owns `chapter_label`, `sort_order`, nullable price override (NULL inherit, 0 free), nullable VIP override, multi-image Cloudinary upload, authoritative manifest ordering/preview, replace/remove and per-file retry. Warn on unsaved navigation and surface validation/mutation failures. Failed pre-commit uploads are compensated; failed post-commit old-asset cleanup is recorded for the orphan cleanup runbook. Cannot Publish Comic without a valid Published Chapter; cannot Hide the last valid Published Chapter of a Published Comic.

## Featured
Select up to 5 Published Comics, drag-drop order with keyboard/up/down controls, preview, atomic save/expected version and 409 conflict UI. Public queries omit Hidden Comics immediately.

## Dashboard
KPIs and charts separating verified VND TopUp/VIP/refunds, Paid/Bonus source, reading and missions activity. Do not interpret coins as cash revenue. Date range and VN timezone explicit.

A04 implements `/admin` with explicit gross TopUp/VIP, completed-refund original/fee/net, Paid/Bonus issuance, qualified sessions/distinct readers, Mission receipt/reward metrics and a Vietnam-local daily table. `/admin/audit` provides date/actor/action/target filters, 30/50/100 page sizes, deterministic pagination, loading/empty/error/retry states and renders only the server-redacted JSON representation. Both caches are scoped to the authenticated Admin identity.

## Finance
Payments/search/detail/reconciliation; read-only Wallet ledger. Refund workspace shows original order amount/fee/net, **exact original PaidCoinLot** granted/spent/refunded/reserved/available, Admin approve/reject and audit timeline, manual transfer reference/evidence, ambiguity/hard failure states. Confirm Completed only from verified operator evidence; not automatically by clicking transfer action.

A02 implements `/admin/payments`, `/admin/ledger` and `/admin/refunds` with server-side filters and 30-item pagination. Payment detail exposes only safe provider evidence. Finance queries are keyed by authenticated Admin identity, and changing the selected refund clears every local note/reference/outcome field before another command can be submitted. Refund recipient fields remain masked; every approve/reject/transfer/complete/release action has explicit pending/error feedback, and financial decisions require confirmation. `NeedsReconciliation` visibly retains its reservation until an explicit Complete or Release decision.

A03 Mission reward history uses server-side 30-item pagination with Previous/Next controls, total item/page feedback, and keeps the selected page through loading, errors and retry. Mission definition inputs follow the domain limits: name at most 200 characters and description at most 2,000 characters.

## Missions / VIP / Users
Mission list + dedicated editor with Fixed/Daily/Weekly/Event, source action, target/reward/repeat, revisions/effective windows; reward history/DeniedDailyCap. VIP plan price edit for future orders; show 720h and 20% as fixed read-only. Users searchable Active/Disabled, detail access/wallet/membership/payments; no direct wallet balance edits and no Admin role edit UI.

A03 Mission activation always submits the currently displayed Mission version and selected immutable revision. VIP price mutation shows pending/error/success and never edits duration/discount. User enable/disable requires confirmation, submits `expectedVersion`, and explains session invalidation. Mission/VIP/User query keys include authenticated Admin identity so cached administrative data is not reused across an identity switch.

## Moderation / Settings / Audit
Admin Hide/Delete/Restore Hidden comment, context view and audit. Typed Settings for approved configurable values; 300 daily Bonus cap fixed read-only; SePay secrets unavailable in UI. Read-only append-only audit filtering time/actor/action/target; redact bank details/secrets.
