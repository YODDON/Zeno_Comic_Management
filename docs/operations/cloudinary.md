# Cloudinary Media & Protected Reader — VERIFICATION REQUIRED

## Asset classes
Comic covers/featured artwork public assets; chapter pages may be Free or locked by Paid/VIP entitlement. Admin multi-upload/reorder, store asset public ID, dimensions, status; avoid orphan assets if DB save fails. Validate the configured byte limit and calculate the content SHA-256 before persisting a `Pending` chapter-page intent with its deterministic provider public ID. After authenticated asset identity and dimensions are confirmed, transition it to `Ready`. A retry with the same fingerprint recovers the deterministic asset through provider metadata and completes the same intent; different content for the same page identity is rejected and never reaches Cloudinary.

## Runtime configuration and pending recovery
Production composition reads `Cloudinary__CloudName`, `Cloudinary__ApiKey`, `Cloudinary__ApiSecret`, optional `Cloudinary__ChapterPagePublicIdPrefix`, and required positive `Cloudinary__MaximumChapterPageBytes`. The maximum is an operator-owned deployment limit until product/provider testing approves a fixed policy; secrets must come from environment/secret storage and never source control.

`Pending` rows are deliberately retained across ambiguous provider or database failures. Retry with the original page ID and identical content first. Operators can identify abandoned intents through the `(asset_status,upload_created_at)` index; do not delete the row or provider asset independently until the provider state has been reconciled. The Admin cleanup UX/API remains part of the later full editor task.

The A01 full editor uses a separate compensated command boundary for append/replace: validate and upload the new authenticated asset first, then lock the owning Comic, Chapter and pages and commit Ready metadata plus audit in one PostgreSQL transaction. Cancellation, provider/unexpected exceptions, validation, stale-version, publication-precondition and database failures after provider success all trigger best-effort deletion of every uncommitted uploaded asset using an independent cleanup cancellation boundary. Replace/remove commit metadata first and only then delete the superseded asset, because external deletion cannot participate in the PostgreSQL transaction. If compensation or post-commit deletion also fails, the API preserves the original result and records structured orphan evidence containing provider name, Cloudinary public ID/provider key, Chapter/page context and correlation identifier, without signed delivery data or secrets. Operators delete only assets that have no `catalog.chapter_pages` row. Never restore the removed DB row merely because provider cleanup failed.

## Critical constraint
Cloudinary ordinary signed URL does **NOT** automatically imply expiration. `private` original assets may not protect derived transformations in every configuration. Paid/VIP pages require proven restricted/time-limited access strategy supported by actual Cloudinary account and transformations. Never publish paid page URLs as static public assets. Signed-only without expiry is not sufficient to claim short-lived entitlement revocation.

R01 therefore uses ordinary signed authenticated-asset URLs only for Free Chapter pages. `VipIncluded` and `CoinUnlocked` pass the logical entitlement check but deliberately receive `503 PROVIDER_UNAVAILABLE` at the manifest boundary until this proof of concept is accepted. This is a fail-secure release blocker, not a temporary public-URL fallback.

## Proof of concept checklist
1. Verify product tier/limits for `authenticated` assets and any token/cookie access control or suitable alternative.
2. Upload protected chapter, test raw URL, derived URL, guessed public_id and copied URL with unauthenticated browser.
3. Measure what happens after VIP expiration, chapter hide, user logout, URL revocation/cache expiry; document actual exposure window.
4. Test images with lazy-load/manifest refresh, mobile sizes, transformations, CDN latency/cost, access errors, orphan cleanup and rollback.
5. Fallback if provider tier cannot meet minimum paid protection: redesign delivery and costs; do not silently use public URLs.

## Release gate
No paid content production until access tests and accepted exposure window are documented with real account/configuration.

The disposable real-account probe and the required browser/API authorization
drill are defined in [Q02 provider verification](../testing/q02-provider-security-poc.md).
The probe never enables protected delivery. It accepts only `401`/`403`/`404`
as verified denial, treats provider/network failures as inconclusive, and
enforces that redacted evidence is written outside the repository without
provider identifiers or signed URLs.
