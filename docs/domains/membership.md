# Membership Module

One VIP 30-day (=720h) plan, starting price 29,000 VND, with a fixed 20% discount for other paid Chapter unlocks. Admin may edit **future-order price only**, not duration or discount for MVP. Each `VipPurchase` PaymentOrder snapshots plan, price, duration and discount; changing the plan never rewrites an existing order.

## Order lifetime and idempotency

VIP checkout timestamps are server-owned. For a new order, the server acquires the idempotency lock and the shared VIP-plan snapshot lock before sampling PostgreSQL time; `expiresAt = createdAt + Membership:VipOrderTtlMinutes`. The default is 30 minutes; configuration must be positive and is validated at startup. This setting is intentionally separate from `Billing:TopUpOrderTtlMinutes` even while both default to 30.

Processing a Pending order when `serverNow >= expiresAt` persists the monotonic `Expired` transition and `expiredAt`. An Expired order never returns to Pending. `(User,VipPurchase,Idempotency-Key)` is permanent: the same key and canonical plan fingerprint replays the original order in its current state, including Expired or Succeeded; a different fingerprint returns `409 IDEMPOTENCY_CONFLICT`; a new purchase requires a new key. The client cannot provide or override order timestamps.

## Fulfillment and late payment

Provider fulfillment locks the per-user subscription and order inside the existing webhook PostgreSQL transaction, then samples `clock_timestamp()`. Auto-fulfillment requires a still-Pending, unexpired exact-amount order and valid subscription/transaction/outbox/provider state. `VipSubscription.Version` is the authoritative monotonic sequence: each success creates exactly one append-only `VipTransaction` with the next `SubscriptionVersion`, updates the subscription to that same version, marks the order/provider event fulfilled and enqueues `Membership.VipMembershipActivated.v1` atomically.

The first fulfillment expires at `fulfilledAt + 720h`; renewal extends from `max(currentExpiry, fulfilledAt)`. Concurrent distinct purchases serialize per user and add exactly one consecutive version and period each. Before extension, the complete version sequence and `PreviousExpiry -> NewExpiry` chain must reconcile to the authoritative subscription. Timestamp/GUID ordering is never used to identify the latest transaction, so equal timestamps or clock rollback cannot reorder renewals. Provider replay creates no second entitlement or outbox effect.

If the order is Expired or processing time is at/after expiry, provider evidence remains durable with reconciliation reason `LatePayment`, but no subscription or VIP transaction is created, no success outbox is emitted, and the order remains Expired. Provider `OccurredAt` is audit evidence only and cannot bypass expiry. Supporting late VIP fulfillment requires a separate approved business rule.

## Entitlement

Active means `expiresAt >` authoritative current time. Active VIP reads Published, Ready-manifest chapters whose effective `vip_eligible=true`; unpublished/Hidden content remains unavailable. Protected Paid/VIP image delivery still fails closed until the Cloudinary protection gate is verified. Other coin-locked chapters receive `ceil(basePrice * 0.8)`; price 101 becomes 81.

## APIs

- `GET /api/v1/membership/plan`
- `GET /api/v1/membership/me`
- `POST /api/v1/membership/checkout`
- `GET/PATCH /api/v1/admin/vip-plan`

Checkout returns a Pending PaymentOrder with provider-specific placeholder instructions. Actual project-account SePay Test-mode behavior remains **Verification Required**. Admin subscription listing is outside V01 and remains owned by the later Admin slice.

## Acceptance

First purchase and renewal use exact 720-hour periods; two concurrent orders each extend once; old order price remains unchanged; duplicate delivery adds no period; expiry invalidates VIP-only access; late/mismatched payment produces evidence without entitlement; transaction rollback leaves no partial subscription, history or outbox state.
