# Missions Module

Five server-authoritative actions: `DailyLogin`, `ChapterReadQualified`, `ComicFollowed`, `ChapterUnlocked`, `WalletTopUpCompleted`. Types: Fixed, Daily, Weekly, Event. Weekly is Monday 00:00 to next Monday 00:00 `Asia/Ho_Chi_Minh`.

## Definition/version and progress
MissionDefinition stable ID; MissionRevision with target, Bonus reward, repeat policy, active period; per user/revision/period MissionProgress. `action_receipts` dedup source event; `progress_actions` dedup relevant subject (chapter, comic or order) within mission/period. A single valid event may contribute to more than one configured Mission. Event-time determines applicable revision, local date and period, not worker processing time. Revisions must not rewrite in-flight progress.

M01 stores definitions as `Draft` or `Active`; activation points to one append-only revision owned by that definition. Repeat-policy values encode the frozen defaults: `OncePerVietnamDate` for DailyLogin and `UniqueSubjectPerPeriod` for subject-bearing actions. Daily periods run from local midnight to local midnight, and weekly periods from Monday 00:00 to the next Monday 00:00 in `Asia/Ho_Chi_Minh`; Fixed/Event use the selected revision's half-open effective window. Revision selection and window membership use the action's authoritative UTC `OccurredAt` (`effectiveFrom <= OccurredAt < effectiveUntil`). Overlapping applicable revision windows are treated as invalid configuration and fail closed.

M01 consumes the existing qualified-read, follow, unlock and fulfilled-TopUp outbox events into `action_receipts`. Receipt insertion and infrastructure processed-message marking share the outbox worker transaction. Receipt source identity is semantic where the producer provides one (ReadingSession, ChapterUnlock or PaymentOrder); Follow uses the unique outbox message because an unfollow/refollow is a new occurrence. Duplicate delivery is idempotent on `(sourceEventId, sourceEventType)`. A receipt starts `Pending`; M02 owns progress processing and transition to `Processed`. `DailyLogin` remains a supported action type, but no Identity producer is introduced by M01.

## Default repeat semantics
DailyLogin one per VN day; ChapterReadQualified one unique chapter per mission period; ComicFollowed one unique Comic per mission period even if unfollow/refollow; ChapterUnlocked one unique Chapter/user event; WalletTopUpCompleted one unique fulfilled PaymentOrder. Policy may constrain target/cooldown; don't invent undisclosed farming allowances.

## Grant
Reward at most 300 Bonus Coins per VN local day/user. Lock `daily_counters(user_id,local_date)` and transact reward grant, Bonus Wallet entry, outbox/inbox dedup atomically. If grant would push above 300, mark `DeniedDailyCap` and grant **0**, no partial/carryover. A completed mission is not always a rewarded mission. Mission reward >300 cannot ever be paid; reject definition in MVP. Worker delay never automatically forfeits legitimate reward; retry persistently, expose alerts and manual operational recovery.

## Acceptance
Concurrent multi-mission grants never exceed 300; delayed 23:59 event counts prior local date, dedup by subject; duplicate processed message does not recredit; revision effective at occurredAt.
