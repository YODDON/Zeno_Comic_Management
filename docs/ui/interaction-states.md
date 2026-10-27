# UI State Matrix

| Flow | Pending | Success | Failure/Conflict |
|---|---|---|---|
| OAuth | Loading session | `/me` confirms identity | Redirect/login error, no fake session |
| Catalog publish | Saving | Backend confirms Published | Show precondition failure and affected Chapters |
| Reader manifest | Loading access | Display authorized pages | Locked/Hidden/retry after re-auth; never show cached unauthorized paid URL by design |
| Coin quote/unlock | Quote expiry countdown; mutation disabled | Ledger/balance refresh on confirmed unlock | `PRICE_CHANGED`, `QUOTE_EXPIRED`, `INSUFFICIENT_COINS`, 409 conflict |
| TopUp/VIP | Pending/order status polling | Wallet/VIP refresh after fulfillment | Reconciliation/unknown, do not mark Failed just because user navigates back |
| Missions | Processing | Granted or DeniedDailyCap clearly distinguished | Worker delay message; no self-claim rewards |
| Refund | Submitted/Review/Reserved/Transfer Pending | Complete after Admin-recorded bank evidence | Rejected, NeedsReconciliation and held reserve; one-request warning |
| Featured reorder | Unsaved state | Saved with version | Concurrent 409 reload/merge/retry |
| Profile | Validation/loading | Name reflected in header | Server validation/401/403 |

Each page must also handle Empty, 401, 403, 404 and offline network retry appropriately.
