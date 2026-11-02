# D01 Deployment and Recovery Evidence

## Automated evidence

The CI job `Deployment build, smoke and restore drill` performs the following against an isolated Compose project:

1. creates short-lived CI-only Data Protection material and non-provider test configuration;
2. validates shell syntax, environment structure and rendered Compose configuration;
3. builds the API, EF migration, React/Caddy and backup images;
4. starts private PostgreSQL, executes the one-shot migration bundle, waits for API readiness and checks the same-origin web route, OpenAPI, liveness and readiness;
5. seeds a deterministic financial fixture spanning fulfilled TopUp, provider transaction/webhook delivery, Wallet, exact PaidCoinLot, immutable ledger, outbox, RefundRequest/recipient, exact-lot CoinReservation and audit evidence;
6. creates an authenticated age-encrypted archive plus signed provenance manifest; proves wrong age key, ciphertext tampering, manifest tampering, wrong provenance key and replacement-ciphertext/forged-checksum attacks fail;
7. proves the restore project has a restore-only network that primary API/PostgreSQL cannot resolve, restores into its ephemeral volume, runs generic recovery verification plus separate exact fixture assertions, and proves cleanup after success and failure;
8. proves backup scheduling/retention/offsite configuration in dry-run mode and verifies systemd unit syntax;
9. pushes two genuinely different local-registry builds, asserts their OCI digests differ, deploys B, rolls back to A by digest, verifies labels/health and switches back to B;
10. tears down all CI containers and volumes.

The normal backend, PostgreSQL/Testcontainers, frontend and real-browser jobs remain independent required evidence. The deployment job does not replace those suites.

## Security boundaries

- PostgreSQL publishes no host port; only Caddy publishes 80/443.
- The API image runs non-root, read-only, with a persistent protected key ring and no-new-privileges.
- Secrets and generated recovery artifacts are ignored by Git. CI recovery artifacts have short retention and contain only CI fixture data.
- Liveness does not query dependencies. Readiness tests cover healthy, unavailable and pending-migration states and expose only the aggregate status.
- Authenticated backup decryption and archive verification occur before destructive SQL; a hard allowlist prevents restoring over primary/localhost/arbitrary targets.
- Release Compose accepts digest-pinned image references from a validated release manifest and contains no production build path. A separate protected GHCR workflow produces real publishable artifacts and an SBOM/provenance-enabled manifest artifact.

## Evidence that remains external

This suite uses placeholder provider configuration with SePay disabled. It is not evidence for live Google OAuth, SePay Test-mode/project-account verification, Cloudinary paid-image protection, production offsite-vendor retention/object lock/alerts, legal/privacy approval, VPS sizing or an actual production deploy. Those gates remain `Verification Required` until their dedicated procedures produce project-owned evidence.
