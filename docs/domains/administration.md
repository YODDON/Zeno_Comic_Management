# Administration Module

Admin UI always Light. Navigation Dashboard; Content (Comics, Chapters, Genres, Authors, Translation Groups, Featured); Users; Finance (Payments, Ledger, Refunds); Benefits (VIP, Missions); Moderation; Settings; Audit.

Advanced dashboard: verified VND payments, TopUp/VIP revenues, refunds, coins sourced Paid vs Bonus, readers/missions activity. Do not treat Bonus Coins as VND revenues or Paid Coins nominal value as revenue. Provide exact metric definitions/date range/timezone.

## Permissions
All Admin endpoints have server role checks; first Admin provision out-of-band, no Admin grant/revoke UI. Users can be disabled according to audit policy, never directly edit Wallet balances. Moderation Hide/Delete/Restore Hidden comments with audit. Restore Deleted disallowed.

## Settings
Reading 90%/15s thresholds may be exposed as typed settings with session policy snapshot if product later enables editing; fixed cap 300 Bonus/day is read-only, no Admin override. VIP 720h/20% fixed MVP, price editable for *new orders* and snapshotted. SePay/Cloudinary secrets never in UI.

## Featured
Up to five Published Comics, drag-drop reorder and keyboard alternative; save atomic with configuration version; public result excludes Comic if later Hidden. Audit admin mutating commands; immutable audit entries redacted of sensitive data.
