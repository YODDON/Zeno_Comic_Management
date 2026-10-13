# API Error & Status Matrix

All errors use ProblemDetails (`type,title,status,detail,errorCode,message,traceId`); `message` is the stable client-facing explanation and currently mirrors redacted `detail`. Hide secrets/hidden resource existence and bank account data.

| HTTP | errorCode | Meaning / UI |
|---|---|---|
| 400 | VALIDATION_FAILED | Display field errors |
| 401 | AUTH_REQUIRED | Google login |
| 403 | FORBIDDEN | No authorization |
| 404 | RESOURCE_NOT_FOUND / CHAPTER_UNAVAILABLE | Not found or hidden; do not expose private metadata |
| 409 | CONCURRENCY_CONFLICT | Reload version; retry consciously |
| 409 | SLUG_CONFLICT | Choose another unique comic slug |
| 409 | IDEMPOTENCY_CONFLICT | Reused key with different request body |
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

### State enums (separate concerns)
Payment provider verification: Pending / Verified / Rejected / ReconciliationRequired; fulfillment: NotStarted / Retryable / Fulfilled / ManualReview. Order expiry is separate `expires_at`. Refund: Requested → UnderReview → RecoveryReserved → TransferPending → Completed OR Rejected / NeedsReconciliation → TransferConfirmedFailed → ReservationReleased. Missions: Progress InProgress/Completed, reward Pending/Granted/DeniedDailyCap. Comic and Chapter Draft/Published/Hidden, republish Hidden after revalidating invariants.

SePay webhook HTTP status/body must follow verified provider contract, **not** this user-facing error table. Invalid signature cannot fulfill.
