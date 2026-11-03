# Deployment Runbook — Single VPS

The deployable remains one ASP.NET Core modular monolith, one React static site and one PostgreSQL database. Caddy is the only public container. PostgreSQL has no host port and the API is reachable only through the Compose networks. Cloudinary and SePay remain external providers.

## Prerequisites and retained gates

- Docker Engine with Compose v2, a domain resolving to the VPS, ports 80/443, UTC clock synchronization and enough persistent storage.
- A Google OAuth client whose authorized callback is `https://<domain>/api/v1/auth/google/callback`.
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
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 \
  -out secrets/backup-provenance-private.pem
openssl pkey -in secrets/backup-provenance-private.pem -pubout \
  -out secrets/backup-provenance-public.pem
cp .env.example .env
chmod 0600 .env secrets/data-protection.* secrets/backup-age.key \
  secrets/backup-provenance-private.pem
chmod 0644 secrets/backup-provenance-public.pem
```

The host PKCS#12 bundle remains owner-only (`0600`). A one-shot, networkless init container copies it into a protected Docker volume as `uid/gid 1654` with mode `0400`; the non-root API mounts that volume read-only. Never make the host private-key bundle world-readable.

Use a password manager or host secret store to generate the database, certificate, backup and provider secrets. `REFUND_FINGERPRINT_KEY_V1` is Base64 for at least 32 random bytes and is independent from Data Protection.

## Validate and deploy immutable images

```sh
sh deployment/scripts/validate-env.sh .env
gh auth token | docker login ghcr.io --username <github-user> --password-stdin
sh deployment/scripts/validate-release-manifest.sh release-manifest.env
sh deployment/scripts/switch-release.sh .env release-manifest.env
```

The `Publish release images` job targets the GitHub Environment `production-release`. Repository owners must configure that environment with required reviewers, prevent self-review and restrict deployment branches/tags before calling the workflow protected. The workflow publishes the separately tagged `api`, `migrator`, `web` and `ops` images to GHCR on an approved tag/manual dispatch, creates a GitHub build-provenance attestation for its digest-pinned manifest, and uploads `release-manifest-<git-sha>`. The manifest records Git SHA, target migration, explicit compatible-schema allowlist, release timestamp and each `image@sha256:...` reference. Verify the attestation with GitHub CLI against this repository before deployment. `switch-release.sh` validates that manifest and performs schema compatibility preflight before pulling or switching any target workload; after health checks it records the actual schema as `ACTIVE_SCHEMA_ID` in `deployment-state/current-release.env`. The human-readable Git-SHA tag is discovery metadata; the digest is the authoritative identity. Never deploy `latest`.

For a local-only build, explicitly layer the development build file:

```sh
docker compose --env-file .env -f compose.yml -f compose.build.yml --profile ops build
docker compose --env-file .env up --detach --wait
BASE_URL=http://localhost sh deployment/scripts/smoke.sh
```

`migrate` is a one-shot EF migration bundle pinned to `CURRENT_MIGRATION_ID` from the validated release manifest. The API cannot start until PostgreSQL is healthy and that exact target succeeds. A compatible application rollback never starts the older migration bundle: it retains the already-active forward schema and switches only API/web after compatibility preflight. It never invokes an EF `Down` migration. Do not run migrations independently from two application instances. `data-protection-init` only establishes ownership on the persistent key-ring volume.

Health endpoints are anonymous but return no application data:

- `/health/live`: process liveness;
- `/health/ready`: PostgreSQL connectivity plus no pending EF migrations.

Both return `Cache-Control: no-store`. Same-origin Caddy routing preserves cookie/CSRF behavior. The configured Compose backend CIDR is the only trusted forwarded-header network.

## Release and rollback

Before every release:

1. require green build/unit/architecture/PostgreSQL/API/frontend/E2E gates;
2. create and copy an authenticated age-encrypted backup offsite;
3. rehearse restore using the exact artifact;
4. verify the repository-owned `production-release` Environment protections, run the publish workflow, verify its attestation, and retain its digest-pinned release manifest;
5. deploy, wait for readiness, then run smoke checks.

Application rollback means invoking `switch-release.sh` with the retained previous release manifest. Normal rollback is forward-schema-only: before pulling, migrating or recreating a workload, the script compares the authoritative active schema with the target release's `COMPATIBLE_SCHEMA_IDS`. A target older than the active schema is rejected unless that exact active schema is explicitly allowed. Destructive/schema rollback is a separate maintenance procedure requiring write stop, operator approval and a verified backup; never reuse the normal switch script or execute an EF down migration blindly. CI builds and pushes release A and B as genuinely different OCI artifacts, deploys A at the penultimate schema, migrates B to the latest schema, asserts every A/B digest differs, proves an incompatible older-schema rollback leaves workload/state unchanged, and proves an explicitly compatible application rollback keeps the latest schema while switching by digest. Preserve incoming provider evidence for reconciliation rather than manually editing Wallet, lot or ledger rows.

Docker base images and GitHub Actions are pinned by digest/commit. Dependabot or a reviewed maintenance change must update pins, rebuild, and rerun D01 before adoption.

## Monitoring

Monitor API 4xx/5xx, readiness, provider mismatch/failure, reconciliation queues, outbox poison/backlog, wallet reconciliation drift, mission grant failures, protected-manifest denials, disk utilization and backup age. Logs and alerts must not contain provider secrets, tokens or refund recipient banking data.

## Capacity boundary

VPS CPU, RAM, disk IOPS, bandwidth and worker throughput remain unknown until a measured staging load test. Generated Compose files are not deployment evidence by themselves.
