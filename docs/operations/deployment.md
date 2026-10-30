# Deployment Runbook — Single VPS

The deployable remains one ASP.NET Core modular monolith, one React static site and one PostgreSQL database. Caddy is the only public container. PostgreSQL has no host port and the API is reachable only through the Compose networks. Cloudinary and SePay remain external providers.

## Prerequisites and retained gates

- Docker Engine with Compose v2, a domain resolving to the VPS, ports 80/443, UTC clock synchronization and enough persistent storage.
- A Google OAuth client whose authorized callback is `https://<domain>/signin-google`.
- A persistent PKCS#12 certificate for ASP.NET Data Protection, a separately generated refund HMAC key, and an age X25519 backup identity. Never commit them.
- Production values copied from `.env.example` into a host-only `.env` with mode `0600`.
- Cloudinary paid-image protection and SePay project-account verification are still **Verification Required**. Keep `SEPAY_WEBHOOK_ENABLED=false` until the Q02 project-account gate is accepted. A successful container smoke test does not satisfy either provider gate.
- Legal/privacy review and measured VPS sizing are release gates, not assumptions made by this runbook.

Generate local secrets, replacing names and passwords for the actual environment:

```sh
mkdir -p secrets backups
openssl req -x509 -newkey rsa:4096 -sha256 -days 825 -nodes \
  -subj "/CN=Comic Management Data Protection" \
  -keyout secrets/data-protection.key -out secrets/data-protection.crt
openssl pkcs12 -export -out secrets/data-protection.pfx \
  -inkey secrets/data-protection.key -in secrets/data-protection.crt
age-keygen -o secrets/backup-age.key
cp .env.example .env
chmod 0600 .env secrets/data-protection.* secrets/backup-age.key
```

Use a password manager or host secret store to generate the database, certificate, backup and provider secrets. `REFUND_FINGERPRINT_KEY_V1` is Base64 for at least 32 random bytes and is independent from Data Protection.

## Validate and deploy immutable images

```sh
sh deployment/scripts/validate-env.sh .env
sh deployment/scripts/switch-release.sh .env <immutable-git-or-digest-tag>
```

The release compose file never builds production images and forbids `latest`. CI publishes the separately tagged `api`, `migrator`, `web` and `ops` images. `switch-release.sh` pulls the requested tag, starts the stack without builds, waits for readiness, runs smoke checks and records the active tag in `deployment-state/current-image-tag`.

For a local-only build, explicitly layer the development build file:

```sh
docker compose --env-file .env -f compose.yml -f compose.build.yml --profile ops build
docker compose --env-file .env up --detach --wait
BASE_URL=http://localhost sh deployment/scripts/smoke.sh
```

`migrate` is a one-shot EF migration bundle. The API cannot start until PostgreSQL is healthy and that job succeeds. Do not run migrations independently from two application instances. `data-protection-init` only establishes ownership on the persistent key-ring volume.

Health endpoints are anonymous but return no application data:

- `/health/live`: process liveness;
- `/health/ready`: PostgreSQL connectivity plus no pending EF migrations.

Both return `Cache-Control: no-store`. Same-origin Caddy routing preserves cookie/CSRF behavior. The configured Compose backend CIDR is the only trusted forwarded-header network.

## Release and rollback

Before every release:

1. require green build/unit/architecture/PostgreSQL/API/frontend/E2E gates;
2. create and copy an authenticated age-encrypted backup offsite;
3. rehearse restore using the exact artifact;
4. build immutable image tags and record the Git commit and image digests;
5. deploy, wait for readiness, then run smoke checks.

Application rollback means invoking `switch-release.sh` with the recorded previous immutable tag. The CI drill tags the built images twice, switches to the rollback tag, verifies the running image and smoke checks, then switches back. Only roll back when the migration is backward compatible. Never execute an EF down migration blindly. If a non-compatible migration has committed, stop writes and follow the tested database restore procedure. Preserve incoming provider evidence for reconciliation rather than manually editing Wallet, lot or ledger rows.

## Monitoring

Monitor API 4xx/5xx, readiness, provider mismatch/failure, reconciliation queues, outbox poison/backlog, wallet reconciliation drift, mission grant failures, protected-manifest denials, disk utilization and backup age. Logs and alerts must not contain provider secrets, tokens or refund recipient banking data.

## Capacity boundary

VPS CPU, RAM, disk IOPS, bandwidth and worker throughput remain unknown until a measured staging load test. Generated Compose files are not deployment evidence by themselves.
