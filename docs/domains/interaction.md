# Interaction Module

Own comic ratings and comments, not `comic_follows` (Reading owns Follow).

## Ratings
One 1–5 star rating per authenticated user per Comic; user can update; return average/count and myRating with comic detail. Unique `(user_id, comic_id)`; count/average always computed accurately or maintained by transactional projection.

## Comments
Target either Comic or Chapter (exclusive XOR). One reply nesting level; reply must share parent target, must not reply to reply. Authenticated owner edit/delete own content. Admin Hide/Delete, plus Restore **Hidden to Visible only** with audit; no resurrection of Deleted. Prevent comments on unexposed target for non-Admin using the shared public-Catalog predicate. Creation uses the Catalog-compatible lock order Comic -> optional Chapter -> readiness pages -> optional parent and revalidates only after locks are held. Root comments and replies are separately paged at a fixed 20 items/page; clients load reply pages explicitly rather than fetching the entire thread. Mutations are optimistic-versioned and limited to 20 requests/minute/authenticated principal.

## Acceptance
User cannot modify others' comments; reply-to-reply is rejected; Hidden is excluded from normal user visibility; restore audited; rating uniqueness survives simultaneous requests.
