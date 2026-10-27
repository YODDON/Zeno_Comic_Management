# Navigation and Layouts

## User
`/` Home Featured manually curated (max 5) + catalog sections; `/comics` Catalog; `/comics/:slug` Detail; `/chapters/:id/read` Reader; `/library` Continue + Followed; `/profile`; `/wallet`; `/topup`; `/vip`; `/missions`; `/refunds` and `/refunds/:id` (routes final naming may be refined by frontend implementation). Auth-required actions redirect to Google OAuth then return where safe. User theme Dark/Light, Reader follows it. Home not personalized for MVP.

## Admin
`/admin` Dashboard, `/admin/comics`, `/admin/chapters`, `/admin/genres`, `/admin/authors`, `/admin/translation-groups`, `/admin/featured`, `/admin/users`, `/admin/payments`, `/admin/ledger`, `/admin/refunds`, `/admin/vip`, `/admin/missions`, `/admin/moderation`, `/admin/settings`, `/admin/audit`.

Admin Light-only sidebar layout; mobile navigation collapses and supports keyboard. Explicit 401/403/404 and pending states. No Admin role assignment controls.
