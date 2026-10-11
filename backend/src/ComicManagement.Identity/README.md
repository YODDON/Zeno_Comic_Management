# Identity I01 operations

I01 implements server-side Google OpenID Connect as the approved Google OAuth login,
an encrypted application cookie, persistent Google `sub` binding, and the authenticated
`GET /api/v1/me` contract. New accounts always start as role `User` with status `Active`.
Email and avatar are provider attributes; email is never used as the identity key.

## Routes

- `GET /api/v1/auth/google/login?returnUrl=/local/path` starts the Google challenge.
- `/api/v1/auth/google/callback` is consumed by the OpenID Connect middleware and must be
  registered as the exact Google redirect URI for each environment.
- `GET /api/v1/me` returns `{id,displayName,email,avatarUrl,role,status}` for an active session.

Only local return URLs are accepted. The callback uses authorization code + PKCE and validates
state, nonce, issuer, audience, signature, lifetime and the exact case-sensitive `sub`. Tokens
are not saved in the application cookie. Existing cookies are revalidated against PostgreSQL on
every request so a disabled account is denied immediately.

I01 intentionally does not expose logout or another authenticated mutation: CSRF token handling,
logout, role policies and Admin provisioning belong to I02. `PATCH /api/v1/me` belongs to I03.

## Required configuration

Supply values through environment variables or a secret store; never commit a client secret:

- `ConnectionStrings__Database`
- `Authentication__Google__ClientId`
- `Authentication__Google__ClientSecret`
- `Authentication__SessionLifetime` (positive `TimeSpan`; no product default)
- `Authentication__DataProtectionKeysPath` (persistent, app-only filesystem/volume path)
- `Authentication__LoginRateLimit__PermitLimit` (`1..1000`)
- `Authentication__LoginRateLimit__Window` (`00:00:01` through `01:00:00`)

The Data Protection directory must persist across deployments. Restrict access to the API
process and protect the volume at rest. The login limiter partitions on the remote IP; a reverse
proxy deployment must configure and trust forwarded headers before relying on client-IP limits.

## Verification

The automated API test uses a local signed OIDC token endpoint and PostgreSQL. It verifies
state/nonce/PKCE, callback replay rejection, secure cookie attributes, safe return URLs,
duplicate subject handling, `/me`, and disabled-account revocation. The production Google client,
HTTPS redirect registration, browser cookie behavior through Caddy, and production key-volume
permissions remain **Verification Required**.
