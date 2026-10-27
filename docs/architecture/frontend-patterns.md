# Frontend Patterns

Organize React into `app`, `pages`, `features/{auth,catalog,reader,library,interaction,wallet,billing,membership,missions,admin}`, `shared/{api,ui,utils}`. Route pages are thin composers; domain features own queries, forms and mutations. Use generated OpenAPI types where available; validation at frontend improves UX but does not replace backend validation.

Auth session derives from `/me`, not from storing access tokens in localStorage. Same-origin cookie + anti-CSRF header/token for mutations. User Theme Dark/Light (initial default Dark proposed), Admin always Light. Reader persists position and handles manifest expiry/error only after reauthorization.

Use consistent error rendering from ProblemDetails `errorCode`; 401 login, 403 permission, 404 hidden/not found, 409 conflicts/price refresh, 422 business errors. Money/coins formatted in vi-VN; UI must not predict payment fulfillment or automatically update visible balance until server confirms.

Implement accessible keyboard alternative to Featured drag-drop; touch targets, skeletons/empty states, error retry, unsaved-change guard in editors. User Library two tabs: Continue and Followed. Profile editable displayName only; Google email read-only.
