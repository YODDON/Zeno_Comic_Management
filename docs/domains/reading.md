# Reading Module

Own `comic_follows`, `reading_sessions`, `reading_progress`, `chapter_unlocks` and reading access projection. Comic/chapter publication is required for public access. Free chapter can be read without login; authenticated users track progress. Coin unlock is durable while content remains public. VIP entitlement is time-bound and **never** inserted as a permanent purchased unlock.

## Access precedence
Reject unpublished/Hidden parent or Chapter first. If effective price is 0 -> Free; if active VIP and effective VIP eligibility -> VipIncluded; else if persistent unlock -> CoinUnlocked; otherwise Locked and quote price may receive active VIP 20% reduction. Chapter price inheritance distinguishes NULL vs 0.

C04 exposes only the anonymous catalog baseline (`Free` for zero effective price, otherwise `Locked`). R01 owns the authenticated batched enrichment to `VipIncluded`/`CoinUnlocked`, the standalone access endpoint and the protected manifest guard.

R01 provides one batched access projection boundary for the C04 Chapter page and rechecks Comic/Chapter publication plus Ready positive-dimension pages in the standalone access/manifest paths. It implements the precedence for Free, active-VIP and durable-unlock signals, while the production VIP and unlock signal sources remain owned by V01 and U02. Missing signals fail closed to Locked rather than granting access.

## Qualification
An authenticated User may create a session only after the R01 access predicate confirms that the Comic and Chapter are Published, at least one positive-dimension `Ready` page exists, and the User is entitled to read. The server creates `started_at`; `expires_at` is the non-sliding value `started_at + 30 minutes`.

Each session snapshots policy revision `r02-v1` (minimum 9000 basis points and 15000ms valid active duration) plus a SHA-256 revision of the ordered Ready-page manifest. Heartbeat sequence numbers start at 1 and increase by exactly one. The server stores the last accepted sequence and canonical request fingerprint: an identical retry is replayed without time or side effects, while a different-payload replay, stale sequence or gap is a conflict.

The client reports only current page order, offset ratio `[0,1]`, and sequence. The first accepted PATCH credits zero time; a later PATCH credits the server-observed interval only when both observations refer to Ready pages, capped at 15 seconds per PATCH. Progress is derived from the current Ready-page ordinal and offset, in basis points, and accepted maximum progress never decreases. If the Ready manifest changes, the server records the new revision and remaps the reported page against the current Ready set; a page no longer Ready cannot advance progress.

Qualification is one-time per ReadingSession and requires accepted progress >=9000 basis points, valid active duration >=15000ms, an unexpired session, and a fresh publication/access check. The session/progress update and `Reading.ChapterReadQualified.v1` outbox write are atomic. The event means only that this session qualified; its payload carries `sessionId`, `userId`, `comicId`, `chapterId`, `qualifiedAt`, policy thresholds/revision, and manifest revision. Downstream consumers decide distinct-chapter/day semantics. These checks reduce abuse but do not prove human attention.

## Continue reading
One user/comic `reading_progress` row with last chapter/page/offset, source session/start and version. A session that started earlier cannot overwrite a newer tab's authoritative progress. History may derive from sessions. Follow unique user/comic, missions dedup by comic+period even after unfollow/refollow.

## Protected media
Auth check before issuing manifest for paid/VIP pages. Provider-specific signed/time-bound delivery **Verification Required**. A Cloudinary normal signed URL is not necessarily expiring. Fail secure until access-control proof-of-concept passes.

R01 never exposes a Cloudinary URL or provider public ID. A Free manifest contains same-origin API content routes; each content request checks access, fetches the authenticated asset internally, validates the owned namespace/raster MIME/maximum size, then rechecks publication, effective price and page asset identity before returning bytes with `no-store` and `nosniff`. A concurrent hide, Free-to-Paid change or asset replacement therefore fails closed. `VipIncluded` and `CoinUnlocked` manifests/content remain provider unavailable until Q02 produces accepted real-account protected-delivery evidence.

The public Chapter list crosses Catalog and Reading projections. R01 retries once when a Catalog item is absent from the authoritative Reading visibility result, then drops any item still missing and adjusts the returned counts conservatively. It does not retain newly private metadata as an `Unavailable` row.

## Acceptance
No manifest for unauthorized Paid; Hidden always denied; VIP expiry triggers new check; no double unlock; qual threshold and active duration both required; stale progress requests handled; reader can retry manifest without opening previously protected assets to unauthorized users.
