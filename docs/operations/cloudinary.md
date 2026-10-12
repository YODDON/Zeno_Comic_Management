# Cloudinary Media & Protected Reader — VERIFICATION REQUIRED

## Asset classes
Comic covers/featured artwork public assets; chapter pages may be Free or locked by Paid/VIP entitlement. Admin multi-upload/reorder, store asset public ID, dimensions, status; avoid orphan assets if DB save fails. Persist a `Pending` chapter-page intent with its deterministic provider public ID before calling Cloudinary. After authenticated asset identity and dimensions are confirmed, transition it to `Ready`. A retry recovers the deterministic asset through provider metadata and completes the same intent; it never creates another page row.

## Critical constraint
Cloudinary ordinary signed URL does **NOT** automatically imply expiration. `private` original assets may not protect derived transformations in every configuration. Paid/VIP pages require proven restricted/time-limited access strategy supported by actual Cloudinary account and transformations. Never publish paid page URLs as static public assets. Signed-only without expiry is not sufficient to claim short-lived entitlement revocation.

## Proof of concept checklist
1. Verify product tier/limits for `authenticated` assets and any token/cookie access control or suitable alternative.
2. Upload protected chapter, test raw URL, derived URL, guessed public_id and copied URL with unauthenticated browser.
3. Measure what happens after VIP expiration, chapter hide, user logout, URL revocation/cache expiry; document actual exposure window.
4. Test images with lazy-load/manifest refresh, mobile sizes, transformations, CDN latency/cost, access errors, orphan cleanup and rollback.
5. Fallback if provider tier cannot meet minimum paid protection: redesign delivery and costs; do not silently use public URLs.

## Release gate
No paid content production until access tests and accepted exposure window are documented with real account/configuration.
