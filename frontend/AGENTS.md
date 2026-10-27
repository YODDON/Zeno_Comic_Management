# Frontend Codex Rules

- React + TypeScript + Vite + Tailwind + shadcn/ui, React Router, TanStack Query, RHF/Zod. Prefer OpenAPI-generated API types. Feature-first directories: `app/`, `pages/`, `features/`, `shared/`.
- User interface: Dark/Light, Reader vertical scroll only and background follows user theme. Admin interface always Light. Design using tokens/components from `docs/ui/design-system.md`.
- Pages compose feature-owned components; do not duplicate business rules in UI. Backend is authority for price, entitlement, payment, balance, qualification, rewards, publication and refunds.
- Display Loading, Empty, Error, Forbidden, Pending and Success states. Never optimistically claim coins/payment/refund succeeded. Restore reading position safely; on image errors recheck entitlement before refreshing manifest.
- Google login only. Email read-only; display name editable. No password form and no Admin grant/revoke controls.
- Before work read `docs/ui/navigation-and-layouts.md`, `docs/ui/user-screens.md` or `docs/ui/admin-screens.md`, plus corresponding API specs.
