# API Error & Status Matrix

All errors use ProblemDetails (`type,title,status,detail,errorCode,message,traceId`); `message` is the stable client-facing explanation and currently mirrors redacted `detail`. Hide secrets/hidden resource existence and bank account data.

| HTTP | errorCode | Meaning / UI |
|---|---|---|
| 400 | VALIDATION_FAILED | Display field errors |
| 401 | AUTH_REQUIRED | Google login |
| 403 | FORBIDDEN | No authorization |
| 404 | RESOURCE_NOT_FOUND / CHAPTER_UNAVAILABLE | Not found or hidden; do not expose private metadata |
| 404 | READING_SESSION_UNAVAILABLE | Session or its unpublished resource is unavailable without leaking private metadata |
| 409 | CONCURRENCY_CONFLICT | Reload version; retry consciously |
| 409 | READING_SEQUENCE_CONFLICT | Heartbeat sequence is stale, has a gap, or reuses a sequence with another payload |
| 409 | READING_SESSION_EXPIRED | Absolute 30-minute session expiry has been reached |
| 409 | SLUG_CONFLICT | Choose another unique comic slug |
| 409 | IDEMPOTENCY_CONFLICT | Reused User + operation + key with a different canonical request; expiry never releases the key |
| 409 | OPERATION_IN_PROGRESS | Poll/retry with same key after Retry-After |
| 409 | QUOTE_EXPIRED | Get fresh five-minute quote |
| 409 | PRICE_CHANGED | Get quote with current pricing |
| 409 | ALREADY_UNLOCKED | Prefer 200 existing entitlement rather than duplicate debit |
| 409 | REFUND_ALREADY_EXISTS | One refund request per TopUp even if Rejected |
| 409 | TRANSFER_REFERENCE_CONFLICT | Reconcile transfer, never silently overwrite |
| 422 | INSUFFICIENT_COINS | Offer TopUp; no debit |
| 422 | REFUND_ORDER_NOT_ELIGIBLE | TopUp condition not met |
| 422 | REFUND_WINDOW_EXPIRED | >7 days after paidAt |
| 422 | REFUND_RECOVERY_INSUFFICIENT | Exact original lot not fully unspent/unreserved |
| 422 | REFUND_TRANSFER_NOT_VERIFIED | Cannot complete refund absent evidence |
| 422 | PUBLICATION_PRECONDITION_FAILED | Must have valid chapter/pages |
| 429 | RATE_LIMITED | Honor Retry-After |
| 503 | PROVIDER_UNAVAILABLE | Do not assume money failed/paid |

Reading session creation/update may return `404 READING_SESSION_UNAVAILABLE` without exposing unpublished content, `403 FORBIDDEN` for a known but unauthorized Chapter, or `409 READING_SESSION_EXPIRED`. A successful non-qualifying `/qualify` response uses `qualified:false` with `reasonCode` equal to `SESSION_EXPIRED`, `PROGRESS_THRESHOLD_NOT_MET`, or `ACTIVE_DURATION_THRESHOLD_NOT_MET`; these are result reasons, not ProblemDetails errors.

Library routes use `401 AUTH_REQUIRED`/`403 FORBIDDEN` for authentication/role failures. PUT Follow returns `404 RESOURCE_NOT_FOUND` when the Comic is not currently public and valid without disclosing draft metadata. Invalid Library cursor/limit or an empty identifier returns `400 VALIDATION_FAILED`; idempotent PUT replay and DELETE unfollow are successful responses rather than conflict errors. Follow-status returns only the current User's ownership state and no Comic metadata.

### State enums (separate concerns)
Payment provider verification: Pending / Verified / Rejected / ReconciliationRequired; fulfillment: NotStarted / Retryable / Fulfilled / ManualReview. TopUp order state is separately persisted as Pending / Expired / Succeeded with `expires_at` and monotonic `expired_at`. Provider transaction processing is Received / ReconciliationRequired / Fulfilled; B03 reconciliation reasons include `ORDER_NOT_FOUND`, `UNSUPPORTED_ORDER_TYPE`, `AMOUNT_MISMATCH`, `LatePayment`, `ORDER_ALREADY_FULFILLED`, `FULFILLMENT_STATE_CONFLICT`, and `PROVIDER_TRANSACTION_CONFLICT`. These are durable provider evidence rather than user-facing ProblemDetails codes. Refund: Requested → UnderReview → RecoveryReserved → TransferPending → Completed OR Rejected / NeedsReconciliation → TransferConfirmedFailed → ReservationReleased. Missions: Progress InProgress/Completed, reward Pending/Granted/DeniedDailyCap. Comic and Chapter Draft/Published/Hidden, republish Hidden after revalidating invariants.

SePay webhook responses are provider acknowledgements, not user-facing ProblemDetails: 200 `{success:true}` for authenticated fulfilled/duplicate/reconciliation evidence; 400 malformed, 401 invalid or stale authentication, 413 oversized body, 429 rate limited, and 503 disabled use `{success:false}` except the framework-generated 429. Invalid authentication is never persisted and cannot fulfill. Late or mismatched authenticated evidence is persisted but never credits Wallet. Actual project-account Test-mode verification remains required before production enablement.
