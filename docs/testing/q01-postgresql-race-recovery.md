# Q01 PostgreSQL race and recovery evidence

Q01 is a quality gate over the existing Wallet, Billing, Reading, Membership,
Refund and Mission vertical slices. It does not introduce a second persistence
model or an in-memory substitute. The **PostgreSQL evidence** column is executed
by the focused profile through Testcontainers and production persistence stores.
The **supplementary evidence** column names unit/API coverage that is executed by
the normal solution test, not by the focused PostgreSQL profile.

## Reproducible profile

Run the focused profile from the repository root:

```powershell
dotnet test backend/tests/ComicManagement.Persistence.IntegrationTests/ComicManagement.Persistence.IntegrationTests.csproj `
  --settings backend/tests/q01-postgresql-race-recovery.runsettings
```

The normal backend solution test remains the completion gate because it also
covers migrations, API contracts and architecture rules. The focused profile
exists for fast, repeatable diagnosis of financial race/recovery failures.

## Matrix traceability

| ID | PostgreSQL evidence in focused profile | Supplementary evidence outside profile |
|---|---|---|
| W01 | `WalletPersistenceTests.Concurrent_first_reads_create_exactly_one_zero_wallet` | — |
| W02 | `TopUpFulfillmentPersistenceTests.Exact_payment_credits_wallet_lot_ledger_and_outbox_once` | — |
| W03 | `ChapterUnlockPersistenceTests.Unlock_is_bonus_first_paid_fifo_atomic_and_replay_safe` | — |
| W04 | `WalletSpendingPersistenceTests.Spend_is_bonus_first_paid_fifo_atomic_and_idempotent` | — |
| W05 | `WalletSpendingPersistenceTests.Insufficient_spend_rolls_back_without_ledger_or_allocation`; `ChapterUnlockPersistenceTests.Insufficient_coins_produces_no_financial_or_entitlement_side_effect` | — |
| W06 | `ChapterUnlockPersistenceTests.Concurrent_unlocks_really_contend_and_only_one_debits` | — |
| W07 | `RefundRequestPersistenceTests.Admin_approval_atomically_reserves_exact_original_lot_and_replays` | — |
| W08 | `RefundRequestPersistenceTests.Verified_transfer_completion_consumes_reservation_once_and_replays` | — |
| W09 | `RefundRequestPersistenceTests.Confirmed_failed_transfer_releases_original_reservation_atomically` | — |
| W10 | `WalletSpendingPersistenceTests.Failure_after_debit_before_commit_rolls_back_all_wallet_changes`; `ChapterUnlockPersistenceTests.Downstream_outbox_failure_rolls_back_debit_ledger_and_entitlement` | — |
| B01 | `TopUpOrderPersistenceTests.PostgreSql_rejects_invalid_topup_amount_status_and_fingerprint` | Billing/API amount-boundary tests |
| B02 | `TopUpFulfillmentPersistenceTests.Concurrent_same_provider_payment_serializes_and_replays_one_credit`; `SePayWebhookPersistenceTests.Concurrent_duplicate_really_contends_on_lock_and_creates_one_transaction` | — |
| B03 | `TopUpFulfillmentPersistenceTests.Amount_mismatch_is_durable_reconciliation_without_credit`; `SePayWebhookPersistenceTests.Database_constraints_reject_invalid_provider_financial_evidence` | Provider HTTP contract tests |
| B04 | `TopUpFulfillmentPersistenceTests.Occurred_before_expiry_but_received_after_expiry_is_late_and_never_credited`; `TopUpFulfillmentPersistenceTests.Server_time_is_sampled_after_financial_lock_wait_and_closes_expired_order` | — |
| V01 | `VipMembershipPersistenceTests.Concurrent_distinct_payments_extend_exactly_twice_and_replay_has_no_effect` | — |
| V02 | `VipMembershipPersistenceTests.Concurrent_distinct_payments_extend_exactly_twice_and_replay_has_no_effect` | — |
| V03 | `VipMembershipPersistenceTests.Concurrent_distinct_payments_extend_exactly_twice_and_replay_has_no_effect` | — |
| V04 | `ChapterAccessProjectionPersistenceTests.Unlock_quote_persists_five_minute_vip_price_and_wallet_estimate` (101 → 81) | `ChapterUnlockQuoteTests.Active_vip_discount_rounds_up_and_estimates_bonus_before_paid`; API quote/access tests |
| RF01 | `RefundRequestPersistenceTests.Eligibility_uses_verified_paid_time_and_full_original_lot` | — |
| RF02 | `RefundRequestPersistenceTests.Request_waiting_on_order_lock_past_seven_day_boundary_fails_closed` | — |
| RF03 | `RefundRequestPersistenceTests.Permanent_idempotency_conflicts_and_one_request_per_order_are_enforced` | — |
| RF04 | `RefundRequestPersistenceTests.Spent_original_lot_is_ineligible_and_request_has_no_financial_mutation` | — |
| RF05 | `RefundRequestPersistenceTests.Approval_waiting_on_wallet_lock_revalidates_lot_and_leaves_no_partial_state`; `Chapter_unlock_holding_original_lot_makes_refund_approval_wait_and_fail_closed` | — |
| RF06 | `RefundRequestPersistenceTests.Concurrent_exact_approval_really_contends_and_reserves_once`; different-key variant proves one winner | — |
| RF07 | `RefundRequestPersistenceTests.Unknown_transfer_holds_reservation_until_explicit_reconciliation` | — |
| RF08 | `RefundRequestPersistenceTests.Concurrent_exact_completion_contends_and_creates_one_financial_resolution`; release and complete-vs-release variants cover both outcomes | — |
| M01 | `M02MissionPersistenceTests.Daily_cap_denies_the_whole_60_coin_reward_after_250_was_granted` | — |
| M02 | `M02MissionPersistenceTests.Concurrent_mission_completions_serialize_on_wallet_and_never_exceed_daily_cap` | — |
| M03 | `MissionPersistenceTests.Qualified_read_receipt_uses_event_time_and_deduplicates_semantic_event`; `M02MissionPersistenceTests.Historical_selection_uses_half_open_boundaries_and_never_current_fallback` | Mission period unit tests |
| M04 | `M02MissionPersistenceTests.Processing_failure_rolls_back_every_side_effect_and_pending_receipt_retries`; `OutboxProcessingTests.Handler_failure_rolls_back_effect_and_retries_once_due` | — |

## Cross-workflow contention and recovery assertions

`Chapter_unlock_holding_original_lot_makes_refund_approval_wait_and_fail_closed`
is the Q01 cross-module race. The unlock holds the production Wallet/PaidCoinLot
critical section while refund approval reaches a real PostgreSQL lock wait. Once
released, unlock commits first and refund approval revalidates the original lot,
returns `RecoveryInsufficient`, and creates no reservation, refund ledger legs or
refund outbox event.

High-risk tests assert the resulting Wallet and PaidCoinLot reconciliation:

- sum of available Paid lot coins equals `wallet.paid_available`;
- sum of reserved Paid lot coins equals `wallet.paid_reserved`;
- immutable ledger/allocation rows match the winning operation;
- failed or replayed operations create no duplicate entitlement, reservation,
  ledger or outbox side effect;
- injected failures are retriable only after the transaction has rolled back.

## Verification boundaries

Q01 proves application and PostgreSQL concurrency/recovery behavior for the
documented provider adapter fixtures. SePay production contract verification and
Cloudinary protected paid-page delivery remain **Verification Required** and are
owned by Q02, not this suite.
