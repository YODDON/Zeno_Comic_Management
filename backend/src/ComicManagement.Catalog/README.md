# Catalog module

Catalog owns comic metadata and taxonomy. In C01 it defines:

- `Comic` metadata with a unique stable slug, series/publication status, cover asset identifier, default coin price, VIP eligibility, timestamps and optimistic-concurrency version.
- `Genre`, `Author` and `TranslationGroup` taxonomy records.
- Many-to-many links from comics to each taxonomy dimension.

Persistence maps these records to the `catalog` PostgreSQL schema through the shared `AppDbContext` and migration stream. Database constraints enforce approved status values, non-negative default prices, unique comic/genre slugs, foreign keys and composite link keys.

Chapter/page storage and Cloudinary upload belong to C02. Publication transitions, valid-chapter checks and slug locking after first publication belong to C03. This module does not expose HTTP endpoints in C01.
