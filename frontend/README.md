# Comic Management frontend

React/TypeScript shell for the Vietnamese Comic Management UI. F05 establishes:

- Vite, React Router, TanStack Query, Tailwind and shadcn-style shared primitives;
- user Dark/Light theme persisted in local storage;
- an isolated Light-only Admin layout;
- a same-origin API client with ProblemDetails, custom headers and idempotency-key support;
- reusable Loading, Empty, Error, Unauthorized, Forbidden, Pending and Success states;
- responsive keyboard navigation and automated accessibility smoke checks.

## Local commands

```bash
npm ci
npm run lint
npm test
npm run test:e2e
npm run build
npm run dev
```

Set `VITE_API_BASE_URL` only when the API is served from a different development origin. The
default empty value keeps requests same-origin. Do not put secrets in `VITE_*` variables because
Vite exposes them to browser code.

F05 does not implement authentication, domain queries or financial workflows. SePay and
Cloudinary provider behavior remains Verification Required.

I03 adds the authenticated `/profile` screen and the `GET/PATCH /api/v1/me` client slice. The
Google email/avatar are read-only; only `displayName` is submitted. The profile cache updates the
header immediately after a successful save, while CSRF tokens are held only in memory and attached
to unsafe same-origin requests. Vitest covers validation/error parsing and Playwright covers the
responsive keyboard flow plus automated accessibility checks.
