# Catalog module

Catalog owns comic metadata and taxonomy. In C01 it defines:

- `Comic` metadata with a unique stable slug, series/publication status, cover asset identifier, default coin price, VIP eligibility, timestamps and optimistic-concurrency version.
- `Genre`, `Author` and `TranslationGroup` taxonomy records.
- Many-to-many links from comics to each taxonomy dimension.

Persistence maps these records to the `catalog` PostgreSQL schema through the shared `AppDbContext` and migration stream. Database constraints enforce approved status values, non-negative default prices, unique comic/genre slugs, foreign keys and composite link keys.

Publication transitions, valid-chapter checks and slug locking after first publication belong to C03. The C01/C02 Catalog model does not expose HTTP endpoints.

## C02 chapter pages and upload boundary

`Chapter` stores a freeform label separately from numeric ordering, nullable price/VIP overrides, publication status and optimistic-concurrency version. `ChapterPage` stores stable ordering, the Cloudinary public ID, provider-independent asset status and intrinsic dimensions.

`IChapterPageAssetStore` is the Catalog-owned port for uploading an image and compensating deletion. The Persistence Cloudinary adapter uploads chapter pages as `authenticated` assets with deterministic public IDs and never returns a provider delivery URL. Protected/time-limited delivery remains Verification Required; C02 does not claim that uploaded assets can yet be served securely to paid/VIP readers.
