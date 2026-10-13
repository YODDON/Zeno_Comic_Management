# Reading module

R01 owns the chapter access decision and manifest guard. `ChapterAccessHandler` applies the documented order: public/valid content first, then Free, active VIP when eligible, durable coin unlock, otherwise Locked. `IChapterAccessProjectionStore` is a batched read port implemented by Persistence; V01 and U02 later provide the production VIP and unlock signals without adding module-to-module references.

Free Chapter manifests contain Ready pages in stable order and expose only same-origin API content routes. The API fetches the authenticated Cloudinary asset internally, validates namespace, raster MIME type and size, and rechecks publication, price and asset identity after the provider fetch before returning `no-store` bytes. Paid/VIP delivery remains a Q02 verification gate, so an entitled protected request fails with provider unavailable rather than receiving the Free delivery path.

Public Chapter list enrichment retries a Catalog/Reading snapshot mismatch once and then removes any still-missing Chapter fail-closed. It never preserves newly hidden Chapter metadata as an `Unavailable` list item.

R02 extends that same access boundary with authenticated 30-minute reading sessions, strict sequence/fingerprint replay handling, Ready-manifest revision tracking, monotonic server-derived progress, cross-tab-safe progress persistence, and one-time session qualification at 90% plus 15 seconds. Qualification and `Reading.ChapterReadQualified.v1` outbox creation are atomic. Follows/Continue, unlock mutation, VIP persistence and frontend reader behavior remain outside R02.
