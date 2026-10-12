# Physical ERD — Field-level Blueprint (unmigrated)

**Status:** field-level blueprint; SQL migrations, FKs, physical indexes and full OpenAPI need implementation verification. Names below are canonical unless explicitly marked proposed. Types: UUID IDs, `timestamptz` UTC, `bigint` money/coins, `version bigint` on mutable aggregates. String statuses validated in application and with CHECK where stable.

## Identity and Catalog
- `identity.users`: `id uuid PK`, `display_name varchar(120) NOT NULL`, `email varchar(320)`, `avatar_url text NULL`, `role varchar(16)`, `status varchar(24)`, `created_at`, `updated_at timestamptz`, `version bigint`.
- `identity.google_identities`: `user_id uuid FK`, `google_subject varchar(255) UNIQUE NOT NULL`, `linked_at timestamptz`.
- `catalog.comics`: `id uuid PK`, `slug varchar(200) UNIQUE`, `title`, `description`, `series_status`, `publication_status`, `first_published_at timestamptz NULL`, `cover_asset_id`, `default_price_coins bigint CHECK>=0`, `default_vip_eligible boolean`, `version`, timestamps.
- `catalog.chapters`: `id uuid PK`, `comic_id uuid FK`, `chapter_label varchar(120)`, `sort_order integer`, `price_override_coins bigint NULL CHECK>=0`, `vip_override boolean NULL`, `publication_status`, `version`, timestamps; unique `(comic_id,sort_order)`.
- `catalog.chapter_pages`: `id uuid PK`, `chapter_id uuid FK`, `page_order integer`, `cloudinary_public_id text`, `asset_status varchar(24)` (`Pending|Ready`), `width integer`, `height integer`; unique `(chapter_id,page_order)` and unique `cloudinary_public_id`. `Pending` is a durable upload intent with zero dimensions; `Ready` requires positive width/height and is the only status valid for publication.
- `catalog.genres(id,slug UNIQUE,name)`, `authors(id,name)`, `translation_groups(id,name)`; join tables composite PK `(comic_id,dimension_id)`.
- `catalog.featured_comics`: `comic_id uuid PK FK`, `display_order integer UNIQUE`, `enabled boolean`; max five and Published eligibility via transactional command; configuration/version aggregate for conflict detection.

## Reading and Interaction
- `reading.reading_sessions`: `id uuid PK`, `user_id uuid FK`, `chapter_id uuid FK`, `started_at`, `last_activity_at`, `active_duration_ms bigint`, `max_progress_basis_points integer CHECK BETWEEN 0 AND 10000`, `qualified_at NULL`, `qualification_policy_snapshot jsonb`, `status`, `expires_at`, `version`.
- `reading.reading_progress`: composite PK `(user_id,comic_id)`, `last_chapter_id uuid FK`, `last_page_order integer`, `last_page_offset_ratio numeric(5,4) CHECK BETWEEN 0 AND 1`, `last_read_at`, `version`; index `(user_id,last_read_at DESC)`.
- `reading.chapter_unlocks`: `id uuid PK`, `user_id uuid FK`, `chapter_id uuid FK`, `wallet_operation_id uuid UNIQUE`, `price_paid_coins bigint`, `unlocked_at`; unique `(user_id,chapter_id)`.
- `reading.comic_follows`: composite PK `(user_id,comic_id)`, `followed_at`; index `(user_id,followed_at DESC)`.
- `interaction.comic_ratings`: composite PK `(user_id,comic_id)`, `stars smallint CHECK 1..5`, timestamps.
- `interaction.comments`: `id uuid PK`, `user_id`, `comic_id uuid NULL`, `chapter_id uuid NULL`, `parent_id uuid NULL`, `body text`, `status`, timestamps, `version`; CHECK exactly one Comic/Chapter target. Application validates parent depth <=1 and same target.

## Billing and Refund
- `billing.payment_orders`: `id uuid PK`, `user_id uuid FK`, `order_code varchar(64) UNIQUE`, `order_type varchar(24)`, `amount_vnd bigint CHECK>0`, `paid_coins_snapshot bigint NULL`, `vip_plan_id uuid NULL`, `vip_duration_hours_snapshot integer NULL`, `vip_discount_percent_snapshot smallint NULL`, `provider varchar(32)`, `provider_payment_status varchar(32)`, `fulfillment_status varchar(32)`, `expires_at timestamptz`, `verified_paid_at NULL`, `fulfilled_at NULL`, `created_at`, `version`. CHECK matching TopUp vs VIP snapshots. Unmatched late payments require reconciliation.
- `billing.provider_transactions`: `id uuid PK`, `provider`, `provider_transaction_id`, `amount_vnd bigint`, `direction`, `matched_order_id uuid NULL`, `verified_at`, `processing_status`; unique `(provider,provider_transaction_id)` once real provider identity verified.
- `billing.webhook_deliveries`: `id uuid PK`, `provider_transaction_id uuid NULL FK`, `received_at`, `auth_verified boolean`, `payload_reference text`, `delivery_reference NULL`, `processing_result`, `error_code NULL`; never dedup financially by delivery ID alone.
- `billing.refund_requests`: `id uuid PK`, `payment_order_id uuid UNIQUE FK`, `user_id`, `reason text`, `original_amount_vnd bigint`, `fee_amount_vnd bigint`, `net_refund_vnd bigint`, `required_recovery_coins bigint`, `status varchar(40)`, `requested_at`, `reviewed_by/at NULL`, `approved_by/at NULL`, `completed_by/at NULL`, `decision_reason NULL`, `version`; at most one request/order even when rejected.
- `billing.refund_recipients`: `id uuid PK`, `refund_request_id uuid UNIQUE FK`, `bank_code`, `account_number_encrypted bytea`, `account_holder_encrypted bytea`, `account_number_last4 varchar(4)`, `encryption_key_version`, `created_at`.
- `billing.refund_transfer_records`: `id uuid PK`, `refund_request_id uuid FK`, `bank_reference NULL`, `amount_vnd bigint`, `status`, `evidence_reference NULL`, `recorded_by`, `recorded_at`, `verified_at NULL`; unique normalized actual bank reference when reliable. Transfer ambiguity keeps reservation.

## Wallet
- `wallet.wallets`: `user_id uuid PK FK`, `paid_available bigint DEFAULT 0 CHECK>=0`, `paid_reserved bigint DEFAULT 0 CHECK>=0`, `bonus_available bigint DEFAULT 0 CHECK>=0`, `version`, timestamps.
- `wallet.paid_coin_lots`: `id uuid PK`, `user_id uuid FK`, `topup_order_id uuid UNIQUE FK`, `granted_coins bigint CHECK>0`, **`spent_coins bigint DEFAULT 0`, `refunded_coins bigint DEFAULT 0`, `reserved_coins bigint DEFAULT 0`**, all >=0, `created_at`, `version`, CHECK `granted_coins >= spent_coins + refunded_coins + reserved_coins` (avoid arithmetic overflow via safe bounds/casts). Index `(user_id,created_at,id)` for FIFO.
- `wallet.wallet_operations`: `id uuid PK`, `user_id`, `operation_type`, `source_type`, `source_id uuid`, `idempotency_key NULL`, `created_at`; unique appropriate `(operation_type,source_type,source_id)` for one-shot business operations; separate phases have distinct operation types.
- `wallet.wallet_entries`: `id uuid PK`, `operation_id uuid FK`, `user_id`, `coin_type` Paid/Bonus, `bucket` Available/Reserved, `delta_coins bigint NOT NULL CHECK<>0`, `leg_order integer`, `created_at`; unique `(operation_id,leg_order)`; append-only.
- `wallet.wallet_allocations`: `id uuid PK`, `operation_id uuid FK`, `paid_coin_lot_id uuid FK`, `amount_coins bigint CHECK>0`, `created_at`; unique `(operation_id,paid_coin_lot_id)`.
- `wallet.coin_reservations`: `id uuid PK`, `refund_request_id uuid UNIQUE FK`, `paid_coin_lot_id uuid FK`, `amount_coins bigint CHECK>0`, `status` Reserved/Consumed/Released, `created_at`, `resolved_at NULL`.

## Membership and Missions
- `membership.vip_plans`: `id uuid PK`, `code varchar(64) UNIQUE`, `name`, `price_vnd bigint CHECK>0`, `duration_hours integer DEFAULT 720`, `discount_percent smallint DEFAULT 20`, `is_active boolean`, `version`, `updated_at`.
- `membership.vip_subscriptions`: `user_id uuid PK`, `expires_at timestamptz`, `last_fulfilled_at`, `version`.
- `membership.vip_transactions`: `id uuid PK`, `payment_order_id uuid UNIQUE FK`, `user_id`, `previous_expiry NULL`, `effective_start`, `new_expiry`, `duration_hours`, `fulfilled_at`, `created_at`.
- `missions.mission_definitions`: `id uuid PK`, `code varchar(80) UNIQUE`, `name`, `description`, `action_type`, `cycle_type`, `status`, `active_revision_id uuid NULL`, `created_at`, `version`.
- `missions.mission_revisions`: `id uuid PK`, `mission_id uuid FK`, `revision_number integer`, `target_count integer CHECK>0`, `reward_bonus_coins bigint CHECK BETWEEN 1 AND 300`, `repeat_policy`, `effective_from`, `effective_until NULL`, `configuration_snapshot jsonb`; unique `(mission_id,revision_number)`.
- `missions.action_receipts`: `id uuid PK`, `source_event_id uuid`, `source_event_type`, `user_id`, `subject_id NULL`, `occurred_at timestamptz`, `occurred_local_date date`, `received_at`, `processing_status`; unique `(source_event_id,source_event_type)`.
- `missions.mission_progress`: `id uuid PK`, `user_id`, `mission_revision_id`, `period_key`, `progress_count integer CHECK>=0`, `status`, `completed_at NULL`, `version`; unique `(user_id,mission_revision_id,period_key)`.
- `missions.progress_actions`: `mission_progress_id uuid FK`, `source_event_id uuid`, `subject_id uuid NULL`, `dedup_key varchar(160)`, `occurred_at`; unique `(mission_progress_id,dedup_key)`.
- `missions.reward_grants`: `id uuid PK`, `progress_id uuid UNIQUE FK`, `user_id`, `bonus_coins bigint`, `outcome` Granted/DeniedDailyCap, `decided_at`, `wallet_operation_id NULL`.
- `missions.daily_counters`: composite PK `(user_id,local_date)`, `granted_total bigint CHECK BETWEEN 0 AND 300`, `version`.

## Admin and infrastructure
- `administration.audit_logs`: `id uuid PK`, `actor_id`, `action`, `target_type`, `target_id`, `correlation_id`, `occurred_at`, `redacted_changes jsonb`.
- `administration.typed_settings`: `key varchar(100) PK`, `value_json jsonb`, `version`, `updated_at`; fixed policies (e.g. 300 cap) not writable.
- `infrastructure.outbox_messages`: `id uuid PK`, `event_type`, `payload jsonb`, `occurred_at`, `available_at`, `status`, `attempt_count`, `locked_until NULL`, `last_error_redacted NULL`; index `(status,available_at)`.
- `infrastructure.processed_messages`: composite PK `(consumer,message_id)`, `processed_at`.
- `infrastructure.idempotency_records`: unique `(actor_scope,operation_type,idempotency_key)` and request fingerprint, response/result reference, TTL policy (proposed implementation detail).

## Implementation notes / not yet verified
EF migrations must create/validate schema, composite FKs, partial/deferrable ordering uniqueness as necessary. Use transactional aggregate guards (Postgres CHECK alone cannot enforce “Comic must have Published Chapter”, Featured <=5 or Wallet balances matching sum of lots). `on delete cascade` prohibited for financial/audit/history entities. Full field defaults/lengths and some provider columns require integration-driven refinement.
