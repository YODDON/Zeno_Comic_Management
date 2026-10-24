# User Screen Specifications

## Home
Manual Featured Banner from Admin, max 5 Published comics ordered; automatic omission of subsequently Hidden entries. Other sections new updates/high ratings; no user-personalized Home modules. Empty Featured -> omit banner and show content sections (fallback proposal).

## Catalog / Comic Detail
Title search, genre, ongoing/completed/hiatus and update ordering; rating sort is a future enhancement and is not part of the current MVP contract. Update order is driven only by the newest valid Published Chapter publication time, never by Comic metadata edits. Cover grid responsive. Comic Detail cover/metadata/genres/author/groups, rating plus paged comments/replies, paged 30 Chapters sorted `sort_order` not label with page/total metadata. Reader shows the Chapter comment section after the final page. Comment forms expose labeled loading/error/retry/pending states; owners can edit/delete root comments and replies. Primary CTA Read from beginning or Continue if history. Access price state batched.

## Library
Auth required. Tabs Continue Reading and Followed. Desktop Continue cards two columns, mobile single; follow grid desktop 4–5/mobile two. Each Continue item displays last Chapter/page progress, access recheck before opening. Hidden/unavailable history may show unavailable state but never reveal pages. Empty states link Catalog; cursor Load More; unfollow after backend confirmation.

## Profile
Google provider avatar/email read-only, editable displayName, status/links to Wallet/VIP, client theme preference, Logout. No password/email change or role editing. Responsive single-column; validate field and show server-driven success.

## Wallet & TopUp
Wallet displays Paid Available, Paid Reserved, Bonus Available, ledger filters/history. TopUp preset 20k/50k/100k/200k/500k (presets proposed), custom 10k–2m steps 1000; show expected Paid Coins; render provider-specific payment instructions, pending polling and reconciliation states. Never optimistic credit.

## VIP
Single 720h plan 29,000 VND initially, eligible chapter access, 20% discount on other coin unlocks; current expiry and renewal history, payment pending; never assume Paid until server fulfilled.

## Missions
Definitions/progress/reward status, auto grants, daily counter 300 VN day, `DeniedDailyCap` explanation, worker pending vs granted and history. No Claim Coins button that mutates server awards.

## Refunds
Dedicated `/refunds` page, select eligible fulfilled TopUp, display original amount, 5% fee/net and seven-day window, bank details at request; clearly warn only one request/order even after rejection and no reserve until Admin approval. Show Requested/Review/Reserved/Transfer Pending/Completed/Rejected/Reconciliation with careful evidence wording.
