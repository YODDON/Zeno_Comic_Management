# Identity I01 operations

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

Only local return URLs are accepted. The callback uses authorization code + PKCE and validates
state, nonce, issuer, audience, signature, lifetime and the exact case-sensitive `sub`. Tokens
are not saved in the application cookie. Existing cookies are revalidated against PostgreSQL on
every request so a disabled account is denied immediately.

I01 applies antiforgery validation only to its logout mutation. General mutation CSRF integration,
role policies and Admin provisioning remain in I02. `PATCH /api/v1/me` belongs to I03.

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
server-side session revocation, trusted forwarded headers/rate-limit partitioning, disabled-account
revocation and encrypted key reuse across app instances. The production Google client, HTTPS
redirect registration, browser cookie behavior through Caddy, production certificate rotation and
key-volume permissions remain **Verification Required**.
