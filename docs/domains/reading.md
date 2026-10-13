# Reading Module

Own `comic_follows`, `reading_sessions`, `reading_progress`, `chapter_unlocks` and reading access projection. Comic/chapter publication is required for public access. Free chapter can be read without login; authenticated users track progress. Coin unlock is durable while content remains public. VIP entitlement is time-bound and **never** inserted as a permanent purchased unlock.

## Access precedence
Reject unpublished/Hidden parent or Chapter first. If effective price is 0 -> Free; if active VIP and effective VIP eligibility -> VipIncluded; else if persistent unlock -> CoinUnlocked; otherwise Locked and quote price may receive active VIP 20% reduction. Chapter price inheritance distinguishes NULL vs 0.

C04 exposes only the anonymous catalog baseline (`Free` for zero effective price, otherwise `Locked`). R01 owns the authenticated batched enrichment to `VipIncluded`/`CoinUnlocked`, the standalone access endpoint and the protected manifest guard.

R01 provides one batched access projection boundary for the C04 Chapter page and rechecks Comic/Chapter publication plus Ready positive-dimension pages in the standalone access/manifest paths. It implements the precedence for Free, active-VIP and durable-unlock signals, while the production VIP and unlock signal sources remain owned by V01 and U02. Missing signals fail closed to Locked rather than granting access.

## Qualification
Backend verifies ≥9000 basis points (90%) and ≥5000ms valid active duration. Session snapshots thresholds/policy revision. Server does not trust arbitrary `qualified=true`, timestamps, 100% position or activity duration reported by client; apply sequence validation, rate limits, plausible page visibility/activity checks and dedup. This is risk reduction, not proof of human attention. Qualified event must carry server-authoritative occurredAt and unique chapter subject.

## Continue reading
One user/comic `reading_progress` row with last chapter/page/offset and version; prevent stale parallel tabs overwriting recent progress. History may derive from sessions. Follow unique user/comic, missions dedup by comic+period even after unfollow/refollow.

## Protected media
Auth check before issuing manifest for paid/VIP pages. Provider-specific signed/time-bound delivery **Verification Required**. A Cloudinary normal signed URL is not necessarily expiring. Fail secure until access-control proof-of-concept passes.

R01 never exposes a Cloudinary URL or provider public ID. A Free manifest contains same-origin API content routes; each content request checks access, fetches the authenticated asset internally, validates the owned namespace/raster MIME/maximum size, then rechecks publication, effective price and page asset identity before returning bytes with `no-store` and `nosniff`. A concurrent hide, Free-to-Paid change or asset replacement therefore fails closed. `VipIncluded` and `CoinUnlocked` manifests/content remain provider unavailable until Q02 produces accepted real-account protected-delivery evidence.

The public Chapter list crosses Catalog and Reading projections. R01 retries once when a Catalog item is absent from the authoritative Reading visibility result, then drops any item still missing and adjusts the returned counts conservatively. It does not retain newly private metadata as an `Unavailable` row.

## Acceptance
No manifest for unauthorized Paid; Hidden always denied; VIP expiry triggers new check; no double unlock; qual threshold and active duration both required; stale progress requests handled; reader can retry manifest without opening previously protected assets to unauthorized users.
