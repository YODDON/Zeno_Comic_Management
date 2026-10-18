# REST API Conventions

`/api/v1`, camelCase JSON. UTC ISO-8601 instants, VND/coin integer counts, UUID identifiers. Public vs User vs Admin scopes declared per route. Cookies HttpOnly/Secure/SameSite, CSRF for authenticated unsafe requests, user authorization by resource and server role. Use rate limits for sensitive endpoints. OAuth callback and SePay webhook are provider-facing exceptions with their own authentication/CSRF rules.

## HTTP
200 query or existing resource result; 201 create; 202 async accepted only if tracking operation exists; 400 validation; 401 unauthenticated; 403 forbidden; 404 hidden/not found; 409 concurrency/idempotency/price/quote expiry; 422 business precondition; 429 throttled; 503 provider unavailable. Never expose secret or hidden content in error `detail`.

## Error wire shape
`{ "type":"https://<own-doc-host>/errors/PRICE_CHANGED", "title":"Conflict", "status":409, "errorCode":"PRICE_CHANGED", "traceId":"...", "detail":"..." }`. Use own stable documentation URI; `<own-doc-host>` is placeholder, not an actual deployment hostname.

## Idempotency
Require `Idempotency-Key` for TopUp/VIP checkout, unlock and sensitive Admin financial commands. Scope keys to principal+operation and hash payload. Same key/same request returns prior result, same key/different request 409; expiry never releases a key. Business unique keys remain mandatory. Prefer `409 OPERATION_IN_PROGRESS` plus `Retry-After` until a durable operation-status resource exists; do not switch arbitrarily to 202. SePay uses provider transaction identity, not browser key. TopUp create, VIP checkout and ChapterUnlockQuote create are each fixed-window limited to 10 requests/minute/authenticated User, partitioned by server-authenticated user ID after authentication; rejection is standardized `429 RATE_LIMITED` with `Retry-After` when supplied by the limiter.

Admin Mission mutations are fixed-window limited to 20 requests/minute/authenticated Admin, partitioned by the server-authenticated Admin user ID after authentication. The limit covers definition creation, revision append, activation and deactivation; read-only Mission routes do not consume it. Rejection uses standardized `429 RATE_LIMITED`.

## Pagination
Public Comic list uses deterministic `(update_sort_at DESC,id DESC)` keyset pagination. Public Chapter list uses one-based `page`, a fixed page size of 30, deterministic `(sort_order,id)` ordering, and response `{items,page,pageSize,totalItems,totalPages}`. Public callers cannot override Chapter page size. A future Admin listing may expose configurable `pageSize`, default 30 and maximum 100. Sorts remain stable on ties.

## Status
Payment verification and fulfillment are separate states. Mis-matched/late provider payment is ReconciliationRequired, not automatically Failed/fulfilled. Mission `DeniedDailyCap` is successful processing but zero reward. Refund eligibility is snapshot until Admin approves under lock.
