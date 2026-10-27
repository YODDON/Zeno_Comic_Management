# Endpoint Inventory (proposed routes; refine OpenAPI as tasks land)

| Actor | Method | Route | Owner |
|---|---|---|---|
| User | GET/PATCH | `/api/v1/me` | Identity |
| Public | GET | `/api/v1/comics`, `/api/v1/comics/{slug}` | Catalog (C04 implemented) |
| Public/User | GET | `/api/v1/comics/{id}/chapters` | Catalog baseline (C04); Reading enrichment (R01) |
| Public | GET | `/api/v1/featured-comics` | Catalog |
| Public/User | GET | `/api/v1/chapters/{id}/access`, `/api/v1/chapters/{id}/pages` | Reading |
| User | POST/PATCH | `/api/v1/reading/sessions`, `/api/v1/reading/sessions/{id}` | Reading |
| User | POST | `/api/v1/reading/sessions/{id}/qualify` | Reading |
| User | GET | `/api/v1/library/continue` | Reading (R03 implemented) |
| User | GET | `/api/v1/reading/history` | Reading (post-MVP/not implemented) |
| User | GET | `/api/v1/library/following` | Reading (R03 implemented) |
| User | PUT/DELETE | `/api/v1/comics/{id}/follow` | Reading (R03 implemented) |
| User | GET | `/api/v1/comics/{id}/follow-status` | Reading (R03 implemented) |
| User | POST | `/api/v1/chapters/{id}/unlock-quotes`, `/api/v1/chapters/{id}/unlock` | Reading (U01 quote); Wallet/Workflows (U02 unlock) |
| User | GET | `/api/v1/wallet`, `/api/v1/wallet/entries` | Wallet |
| User | POST | `/api/v1/billing/topups` | Billing |
| User | GET | `/api/v1/billing/orders/{id}` | Billing |
| SePay | POST | `/api/v1/webhooks/sepay/ipn` | Billing adapter (B02 ingestion; B03 TopUp and V01 VIP fulfillment implemented) |
| User | GET | `/api/v1/membership/plan`, `/api/v1/membership/me` | Membership (V01 implemented) |
| User | POST | `/api/v1/membership/checkout` | Membership/Billing (V01 implemented) |
| User | GET | `/api/v1/missions`, `/api/v1/missions/daily-limit`, `/api/v1/missions/rewards` | Missions (M02 implemented) |
| User | GET/POST | `/api/v1/refunds`, `/api/v1/refunds/eligibility` | Billing |
| User | GET | `/api/v1/refunds/{id}` | Billing |
| Public/User | GET/PUT/DELETE | `/api/v1/comics/{comicId}/rating` | Interaction (IN01 implemented) |
| User | GET/POST/PATCH | Comments endpoints defined in Interaction vertical slice | Interaction |
| Admin | POST | `/api/v1/admin/comics/{comicId}/publish`, `/api/v1/admin/comics/{comicId}/hide`, `/api/v1/admin/chapters/{chapterId}/publish`, `/api/v1/admin/chapters/{chapterId}/hide` | Catalog |
| Admin | PATCH | `/api/v1/admin/comics/{comicId}/slug` | Catalog |
| Admin | GET/POST/PATCH | `/api/v1/admin/comics`, `/api/v1/admin/chapters` | Catalog (A01 implemented) |
| Admin | GET/POST/PATCH/DELETE | `/api/v1/admin/taxonomy/{kind}`, `/api/v1/admin/taxonomy/{kind}/{id}` | Catalog (A01 implemented) |
| Admin | GET/POST/PUT/DELETE | `/api/v1/admin/chapters/{chapterId}/pages...` manifest/append/reorder/replace/remove/preview | Catalog (A01 implemented) |
| Admin/Public | GET/PUT, GET | `/api/v1/admin/featured`, `/api/v1/featured` | Catalog (A01 implemented) |
| Admin | GET/PATCH | `/api/v1/admin/vip-plan` | Membership (V01 implemented) |
| Admin | GET/POST | `/api/v1/admin/missions`, `/api/v1/admin/missions/{id}`, `/api/v1/admin/missions/{id}/revisions`, `/api/v1/admin/missions/{id}/activate`, `/api/v1/admin/missions/{id}/deactivate` | Missions (M01 implemented) |
| Admin | POST review/approve/reject/transfer-attempts/complete/release | `/api/v1/admin/refunds/{id}/...` | Billing/Wallet workflow (RF03 implemented) |
| Admin | GET | `/api/v1/admin/payments`, `/api/v1/admin/ledger`, `/api/v1/admin/audit-logs`, `/api/v1/admin/dashboard` | Billing/Wallet/Admin |

The SePay webhook wire contract is implemented and covered by automated official-contract fixtures, while actual project-account Test-mode/provider verification remains pending. Other provider routes and all Admin CRUD variants remain wire-contract work, not assumed tested APIs.
