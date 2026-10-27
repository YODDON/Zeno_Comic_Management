# Schema Ownership

| Schema | Primary owner | Representative tables |
|---|---|---|
| identity | Identity | users, google_identities |
| catalog | Catalog | comics, chapters, chapter_pages, genres, authors, translation_groups, junctions, featured_comics |
| reading | Reading | reading_sessions, reading_progress, chapter_unlocks, comic_follows |
| interaction | Interaction | comic_ratings, comments |
| billing | Billing | payment_orders, provider_transactions, webhook_deliveries, refund_requests, refund_recipients, refund_transfer_records |
| wallet | Wallet | wallets, paid_coin_lots, wallet_operations, wallet_entries, wallet_allocations, coin_reservations |
| membership | Membership | vip_plans, vip_subscriptions, vip_transactions |
| missions | Missions | mission_definitions, mission_revisions, action_receipts, mission_progress, progress_actions, reward_grants, daily_counters |
| administration | Administration | audit_logs, typed_settings |
| infrastructure | Host/Persistence | outbox_messages, processed_messages, idempotency_records |

One `AppDbContext` and one EF Core migration stream. FK across schemas is allowed in shared Postgres but module/application boundaries are enforced by code. Do not cascade hard-delete histories, ledger, payments, ratings or annotations if referenced by financial/read activity.
