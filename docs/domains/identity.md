# Identity Module

## Scope
Google OAuth only; users default role User; Admin provisioned outside web UI through controlled, audited operational procedure. Store Google provider subject as stable identity, unique; email and avatar are provider attributes, not login secrets. A disabled account must lose access to protected endpoints.

## Core schema
`identity.users(id uuid PK, display_name varchar(120), email varchar(320), avatar_url text NULL, role varchar(16), status varchar(24), created_at timestamptz, updated_at timestamptz, version bigint)`; `identity.google_identities(user_id uuid FK, google_subject varchar(255) UNIQUE, linked_at timestamptz)`.

## HTTP
`GET /api/v1/me` session profile; `PATCH /api/v1/me` updates only displayName with trim/length validation, updates header query; OAuth auth/callback/logout provider-specific routes. Do not supply change-password or change-email routes. Admin Users list/detail, enable/disable with audit; no Admin promote/demote UI/API command.

## Controls
Secure HttpOnly SameSite cookie, anti-CSRF for mutations, persisted Data Protection keys, OAuth state/nonce validation, session revocation/logout, issuer/audience verification and exact Google `sub` binding. Admin first-account provisioning documented in Operations, with least privilege.

## Acceptance
Unauthenticated protected calls = 401; user to admin command = 403; replayed Google callback rejected; duplicate OAuth user does not duplicate identity; profile changes cannot edit role/email/sub.
