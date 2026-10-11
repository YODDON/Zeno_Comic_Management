# Comic Management frontend

React/TypeScript shell for the Vietnamese Comic Management UI. F05 establishes:

- Vite, React Router, TanStack Query, Tailwind and shadcn-style shared primitives;
- user Dark/Light theme persisted in local storage;
- an isolated Light-only Admin layout;
- a same-origin API client with ProblemDetails, custom headers and idempotency-key support;
- reusable Loading, Empty, Error, Pending and Success states.

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
