# P0 Request/Response Contracts — Implementation Blueprint

**Status:** approved business behavior with *proposed* wire schemas. Concrete SePay payload/QR fields and Cloudinary protected manifest are Verification Required. Implement OpenAPI and tests before treating routes production-ready. `Idempotency-Key` required on financial POST commands. Actor scopes indicated.

## Identity — GET/PATCH `/api/v1/me` (User)
GET -> `{id,displayName,email,avatarUrl,role,status}`. PATCH request `{displayName}` updates only displayName; never provider subject/email/role. 400 invalid name, 401 no session, 403 disabled. Cookie Google OAuth only.

## Catalog & chapter access (Public/User)
GET `/api/v1/comics?search=&genre=&seriesStatus=&cursor=&limit=30` -> `{items:[{id,slug,title,coverAssetId,seriesStatus,updateSortAt,genres:[{id,name,slug}]}],nextCursor}`. Search is case-insensitive literal title search; `genre` is a genre slug; `seriesStatus` is `Ongoing|Completed|Hiatus`; default limit is 30 and valid limits are 1â€“100. Results contain only Published comics with at least one valid Published Chapter and use deterministic `(update_sort_at DESC,id DESC)` keyset pagination. `updateSortAt` is the greatest `published_at` among valid Published Chapters; if no Published Chapter exists, its internal fallback is `comic.created_at`. Admin-only Comic metadata edits update `comic.updated_at` but never affect reader update ordering. Draft, Hidden, deleted or scheduled/unpublished Chapters never contribute.

GET `/api/v1/comics/{slug}` -> `{id,slug,title,description,coverAssetId,seriesStatus,firstPublishedAt,updatedAt,genres:[{id,name,slug}],authors:[{id,name,slug:null}],translationGroups:[{id,name,slug:null}]}`; Draft/Hidden/invalid comics return 404 without private metadata.

GET `/api/v1/comics/{id}/chapters?page=1` -> `{items:[{id,chapterLabel,sortOrder,publicationStatus,accessSummary:{accessType,canRead,finalPriceCoins}}],page,pageSize:30,totalItems,totalPages}`; `page` is one-based and public page size is fixed at 30. The route returns only valid Published Chapters of a Published parent, ordered deterministically by `(sort_order,id)`. C04 provides an anonymous baseline in one batched query: effective price `0` is `Free/canRead=true`; positive effective price is `Locked/canRead=false`; `finalPriceCoins` is `chapter.price_override_coins ?? comic.default_price_coins`. R01 owns authenticated `VipIncluded`/`CoinUnlocked` enrichment and must replace this baseline without per-chapter HTTP calls. C04 never returns Chapter page/provider identifiers.

GET `/api/v1/chapters/{id}/access` -> `{chapterId,accessType:"Free|VipIncluded|CoinUnlocked|Locked|Unavailable",canRead,priceCoins,needsLogin}`. GET `/api/v1/chapters/{id}/pages` only after access check: `{chapterId,pages:[{order,width,height,deliveryUrl}],manifestIssuedAt,refreshHint}`. For R01 Free content, `deliveryUrl` is the same-origin GET `/api/v1/chapters/{id}/pages/{pageOrder}/content`, not a Cloudinary/provider URL. The content route repeats the access check, returns a supported raster image with `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`, and uses the same `401`, `403`, `404` and `503` error contract. Protected Paid/VIP delivery remains unresolved and must fail secure.

R01 implements these GET routes anonymously so Free content can be evaluated and read without login. Unpublished/Hidden/invalid content returns `404 CHAPTER_UNAVAILABLE`. A Locked anonymous manifest or content request returns `401 AUTH_REQUIRED`; a signed-in user without entitlement receives `403 FORBIDDEN`. Free manifests contain only ordered Ready pages and are `Cache-Control: no-store`. The content route fetches provider bytes internally and rechecks publication, effective price and page asset identity after the fetch, so a concurrent hide/reprice/asset replacement fails closed without returning bytes. Chapter-list enrichment retries one Catalog/Reading visibility mismatch and removes an item that is still missing, rather than exposing stale hidden metadata as `Unavailable`. Although the access evaluator supports `VipIncluded` and `CoinUnlocked` precedence, V01/U02 own their persistent production sources. Until Q02 verifies protected Cloudinary delivery, an otherwise authorized Paid/VIP manifest/content request returns `503 PROVIDER_UNAVAILABLE` instead of exposing a Free/shareable delivery path.

## Catalog publication and slug (Admin)
POST `/api/v1/admin/comics/{comicId}/publish`, POST `/api/v1/admin/comics/{comicId}/hide`, POST `/api/v1/admin/chapters/{chapterId}/publish`, and POST `/api/v1/admin/chapters/{chapterId}/hide` accept `{expectedVersion}` and return 200 `{id,publicationStatus,version,firstPublishedAt}`. PATCH `/api/v1/admin/comics/{comicId}/slug` accepts `{slug,expectedVersion}` and returns 200 `{id,slug,version}`. All routes require an authenticated Admin and CSRF token. They return 400 `VALIDATION_FAILED`, 404 `RESOURCE_NOT_FOUND`, and 409 `CONCURRENCY_CONFLICT`; publication/slug-lock invariant failures return 422 `PUBLICATION_PRECONDITION_FAILED`, while an existing slug returns 409 `SLUG_CONFLICT`. These non-financial commands use optimistic versions rather than `Idempotency-Key`. Mutations and their redacted immutable audit rows commit in the same PostgreSQL transaction.

## Reading session (User)
All session routes require an authenticated User and the global CSRF policy. POST `/api/v1/reading/sessions` accepts `{chapterId}` and returns 201 `{sessionId,startedAt,expiresAt,manifestRevision,manifestPageCount,policy:{revision,minProgressBasisPoints:9000,minActiveDurationMs:15000},version}`. The server supplies the timestamps, uses an absolute non-sliding 30-minute expiry, and creates the session only when the R01 predicate confirms a Published Comic/Chapter, at least one Ready page, and current read entitlement.

PATCH `/api/v1/reading/sessions/{id}` accepts `{currentPageOrder:int>=1,currentPageOffsetRatio:number[0,1],sequenceNumber:long>=1}` and returns 200 `{sessionId,acceptedPosition:{pageOrder?,pageOffsetRatio?},acceptedMaxProgressBasisPoints,activeDurationMs,manifestRevision,manifestPageCount,lastAcceptedSequence,expiresAt,version}`. Progress is derived from current Ready-page ordinal plus offset. Sequence begins at 1 and must increment by one; same sequence and canonical payload is an idempotent replay, while payload mismatch, stale sequence, and gap return 409 `READING_SEQUENCE_CONFLICT`. The first accepted PATCH credits no active time; subsequent accepted heartbeats credit server elapsed time up to 15 seconds each. The endpoint is limited to 60 requests/minute/User by default.

When the Ready-page revision changes, the server remaps the reported page on the current Ready set, records the new revision, and never decreases accepted maximum progress. A no-longer-Ready page cannot advance progress. POST `/api/v1/reading/sessions/{id}/qualify` returns 200 `{qualified,qualifiedAt?,reasonCode?,version}`. Qualification requires >=9000 basis points, >=15000ms valid active time, an unexpired session, and current publication/access. Session/progress mutation and the one-time `Reading.ChapterReadQualified.v1` outbox event are committed in one transaction. GET `/reading/continue` remains owned by R03. Nonqualifying progress creates no Mission event.

## Wallet (User)
GET `/api/v1/wallet` -> `{paidAvailable,paidReserved,bonusAvailable,totalAvailable,version}`; totalAvailable excludes reserved. GET `/wallet/entries?cursor=` -> immutable ledger projections with operation type, coin/bucket, delta, occurredAt, cursor. Never expose other users' data.

## Unlock Quote (User)
POST `/api/v1/chapters/{id}/unlock-quotes` no client price -> `{quoteId,chapterId,basePriceCoins,vipDiscountApplied,finalPriceCoins,bonusToSpend,paidToSpend,expiresAt,priceRevision}`; expires in 5 minutes, bound to user/chapter/revision. Example price 101 with VIP discount -> ceil(80.8)=81. Quote never reserves funds. Locked only; Free/VIP included bypass purchase.

## Unlock (User)
POST `/api/v1/chapters/{id}/unlock`, `Idempotency-Key`, `{quoteId}` -> 200 `{chapterId,accessType:"CoinUnlocked",spent:{bonusCoins,paidCoins,totalCoins},walletAfter:{paidAvailable,bonusAvailable},alreadyUnlocked}`. Transaction rechecks quote, publication, entitlement, price and balance; Bonus then Paid FIFO; writes ledger + allocation + unique chapter unlock + outbox. Retry safe. 409 `QUOTE_EXPIRED`, `PRICE_CHANGED`, `IDEMPOTENCY_CONFLICT`; 422 `INSUFFICIENT_COINS`; hidden 404.

## TopUp (User)
POST `/api/v1/billing/topups`, `Idempotency-Key`, `{amountVnd:100000}` -> 201 `{orderId,orderCode,amountVnd:100000,paidCoinsExpected:10000,status:"Pending",expiresAt,paymentInstructions:{type:"ProviderSpecific"}}`. Validate 10k–2m VND, multiple 1000. `paymentInstructions` is a **placeholder union** until actual SePay QR/checkout verified. GET `/billing/orders/{id}` returns owner-safe providerPaymentStatus, fulfillmentStatus, matching/reconciliation state. Redirect alone never fulfills.

## SePay webhook (Provider)
POST `/api/v1/webhooks/sepay/ipn`: read raw body, authenticate via account-validated method (HMAC preferred subject to real verification), anti-replay, normalize provider financial transaction ID, validate payment code/receiving account/incoming direction/exact amount/expiry policy. Financial event dedup independent of delivery retry. One transaction fulfills TopUp (Paid lot+Wallet+ledger+outbox) OR VIP (membership transaction+subscription+outbox). Unmatched, late, wrong-amount -> reconciliation with no automatic credit. **HTTP acknowledgement, headers and exact payload MUST be confirmed with SePay documentation/test account.**

## VIP (User/Admin)
GET `/membership/plan` -> `{planId,priceVnd,durationHours:720,discountPercent:20,version}`. GET `/membership/me` -> `{isActive,expiresAt}`. POST `/membership/checkout`, `Idempotency-Key`, `{planId}` -> 201 `{orderId,amountVnd,durationHours:720,status:"Pending",paymentInstructions}`. Fulfill exactly once; expiry=max(existingExpiry,successfulBackendFulfilledAt)+720h. PATCH Admin `/admin/vip-plan` `{priceVnd,expectedVersion}` changes **future-order price only**; duration/discount fixed for MVP.

## Refund eligibility (User)
GET `/api/v1/refunds/eligibility?paymentOrderId={id}` -> `{orderId,originalAmountVnd,feeVnd,netRefundVnd,lotGrantedCoins,lotAvailableCoins,eligible,reasonCode,paidAt}`. Informational snapshot only; lot rechecked when Admin approves. Within 7 days of verified paidAt. 5% fee VND is whole due to TopUp multiples 1000.

## Refund request (User)
POST `/api/v1/refunds`, `Idempotency-Key`, `{paymentOrderId,reason,recipient:{bankCode,accountNumber,accountHolder}}` -> 201 `{refundId,status:"Requested",originalAmountVnd,feeVnd,netRefundVnd,requestedAt}`. Recipient encrypted and masked in responses/logs. One RefundRequest per TopUp **even if Rejected**; no reserve on submission.

## Refund Admin transitions
`POST /api/v1/admin/refunds/{id}/review`: UnderReview.
`POST /approve` `{expectedVersion,decisionNote}` -> `RecoveryReserved` with reservation; lock Wallet/lot, recheck full `granted_coins` still available (spent/refunded/reserved=0), reserve all; 422 insufficient, 409 conflict.
`POST /reject` `{expectedVersion,reason}` -> Rejected and audit.
`POST /transfer-attempts` records manual attempt details/reference/evidence and state, does **not** itself prove bank succeeded.
`POST /complete` `{expectedVersion,transferRecordId,confirmationNote}` -> Completed only after verified/reviewed bank evidence; consumes reserved to refunded, immutable ledger. `POST /release` after **confirmed failed** transfer -> release to Paid available; ambiguous `NeedsReconciliation` holds reservation. Never automatically retry bank payout.

## Missions (User/Admin)
GET `/api/v1/missions` -> active definitions/revisions plus per-period progress/rewardStatus; GET `/daily-limit` -> `{localDate,limitCoins:300,grantedCoins,remainingCoins}`; GET `/rewards` -> cursor history. Admin create/revise/activate Missions with target, reward (<=300), action type, Fixed/Daily/Weekly/Event, repeat policy and effective window. **No User API to grant own reward**. Backend event -> dedup -> period/revision at event time -> reward decision + Wallet Bonus+ledger+counter transaction; `DeniedDailyCap` is processed result, not HTTP failure.

## Error conventions
Errors expose normalized `{errorCode,message,traceId}` fields in addition to standard Problem Details fields. Use `docs/api/error-codes.md` for HTTP mapping and domain guard. Financial request idempotency stores request fingerprint, prior result and in-progress state. For same-key operation executing, default `409 OPERATION_IN_PROGRESS` + Retry-After unless durable status resource introduced by approved ADR.
