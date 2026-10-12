# Identity operations

I01 implements server-side Google OpenID Connect as the approved Google OAuth login,
an encrypted application cookie, persistent Google `sub` binding, and the authenticated
`GET /api/v1/me` contract. New accounts always start as role `User` with status `Active`.
Email and avatar are provider attributes; email is never used as the identity key.

## Routes

- `GET /api/v1/auth/google/login?returnUrl=/local/path` starts the Google challenge.
- `/api/v1/auth/google/callback` is consumed by the OpenID Connect middleware and must be
  registered as the exact Google redirect URI for each environment.
- `POST /api/v1/auth/google/logout` requires the authenticated session and the antiforgery
  token returned in the `X-CSRF-TOKEN` response header by `GET /api/v1/me`. Logout increments
  the user's session version, invalidating every copy of an older session cookie.
- `GET /api/v1/me` returns `{id,displayName,email,avatarUrl,role,status}` for an active session.
- `PATCH /api/v1/me` accepts only `{displayName}`, requires the session antiforgery token, and
  returns the same profile shape. It trims the name, validates a maximum of 120 Unicode scalar
  values, uses the user version for optimistic concurrency, and refreshes the current cookie
  claims without extending the original session lifetime. Because the same version is the
  server-side session-revocation epoch in the current Identity model, a successful name change
  deliberately invalidates older cookies on other browsers/devices; those sessions must sign in
  again. This behavior is covered by the PostgreSQL/API session tests and must not be changed
  without an approved session-model decision.

Only local return URLs are accepted. The callback uses authorization code + PKCE and validates
state, nonce, issuer, audience, signature, lifetime and the exact case-sensitive `sub`. Tokens
are not saved in the application cookie. Existing cookies are revalidated against PostgreSQL on
every request so a disabled account is denied immediately.

I02 applies centralized antiforgery validation to every authenticated `POST`, `PUT`, `PATCH` and
`DELETE` controller action. Anonymous provider-facing requests remain outside session CSRF and
must apply their documented provider authentication. The API exposes reusable User and Admin
authorization policies; the User policy admits both active roles while the Admin policy requires
the `Admin` role. There is no web API or UI for changing roles.

The controlled, one-time first-Admin procedure is documented in
`backend/operations/README.md` and implemented by `backend/operations/provision-first-admin.sql`.
It is deliberately an out-of-band, reviewed database operation.

## Required configuration

Supply values through environment variables or a secret store; never commit a client secret:

- `ConnectionStrings__Database`
- `Authentication__Google__ClientId`
- `Authentication__Google__ClientSecret`
- `Authentication__SessionLifetime` (positive `TimeSpan`; no product default)
- `Authentication__DataProtectionKeysPath` (persistent, app-only filesystem/volume path)
- `Authentication__DataProtectionCertificatePath` (persistent PKCS#12 certificate used to
  encrypt the key ring at rest)
- `Authentication__DataProtectionCertificatePassword` (secret; never commit it)
- `Api__ErrorDocumentationBaseUri` (absolute HTTPS base URI ending at the error documentation path)
- `Authentication__LoginRateLimit__PermitLimit` (`1..1000`)
- `Authentication__LoginRateLimit__Window` (`00:00:01` through `01:00:00`)
- `ReverseProxy__KnownProxies__{index}` and/or `ReverseProxy__KnownNetworks__{index}` for the
  exact trusted Caddy addresses/CIDRs. Do not trust arbitrary forwarded headers.

The Data Protection directory and certificate must persist across deployments and be backed up
separately. Restrict both to the API process. Forwarded headers are processed before auth; the
login limiter partitions on the forwarded client IP only when the immediate proxy matches the
configured trusted proxy/network list.

## Verification

The automated API test uses a local signed OIDC token endpoint and PostgreSQL. It verifies
state/nonce/full PKCE exchange, negative token validation, callback replay rejection, secure
cookie attributes, safe return URLs, duplicate subject handling, `/me`, CSRF-protected logout,
server-side session revocation (including older sessions after a profile version change), trusted forwarded headers/rate-limit partitioning, disabled-account
revocation, centralized mutation CSRF, User/Admin authorization responses and encrypted key reuse
across app instances. I03 additionally verifies profile validation, immutable provider/role fields,
current-session continuity, mutation CSRF, real PostgreSQL optimistic-concurrency races and
concurrency-conflict mapping. The production Google client, HTTPS
redirect registration, browser cookie behavior through Caddy, production certificate rotation and
key-volume permissions remain **Verification Required**. The first-Admin SQL procedure also needs
a production operations dry run with the real least-privilege database role before that gate can
be marked verified.
