# Catalog Module

## Model
Comic with title, unique stable slug, description, cover, genres/authors/translation groups (separate M:N), publication Draft/Published/Hidden, series Ongoing/Completed/Hiatus, default price and VIP eligible. Chapter has freeform `chapter_label`, numeric `sort_order`, nullable price override (`NULL` inherit, `0` free), nullable VIP override, publication state; ordered valid pages via Cloudinary. A chapter page asset is `Pending` while its durable upload intent exists but provider metadata is not confirmed, and `Ready` only after the authenticated Cloudinary asset identity and positive width/height are confirmed. Only `Ready` pages are valid for publication. Featured max five Published Comics with ordered positions.

## Publication
Chapter may become Published under Draft Comic, but not visible. Comic Publish requires >=1 Published Chapter with >=1 valid page. Do not Hide last valid Published Chapter while Comic Published; editing/removing page may not invalidate this either. Slug permanently immutable after *first* publish (`first_published_at`). Comic/Chapter with read/payment history cannot be hard-deleted; use Hidden. Public exposure requires both parent Comic and Chapter Published.

## API
Public comic search/filter/sort, detail, chapter Load More 30, featured. Admin comics/chapters CRUD, publish/hide, upload page ordering, metadata/taxonomy, Featured GET/PUT with version. Chapter list response includes batched access projection instead of one HTTP query per chapter. Save reorder atomically, account for unique-position collisions.

## Acceptance
Cannot publish Comic with zero valid Published Chapters; cannot publish empty Chapter; can publish Chapter while Comic Draft without public access; hide last valid Published Chapter rejected; paid assets never leaked in public metadata; Featured duplicates/max6/unpublished rejected; concurrent reorder conflicts rather than lost updates.
