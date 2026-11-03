# Backup, Restore and Incident Procedures

## Encrypted backup

The `ops` profile runs `pg_dump` in custom format, verifies its catalog and encrypts it with authenticated `age` X25519 encryption. Plaintext dumps are removed on every exit path. It also signs a manifest with a backup-only RSA private key. The manifest binds ciphertext hash, source environment/database, Git SHA, authoritative release timestamp, API/web/migrator digests, migration ID, backup creation time and format. Restore verifies the signature and ciphertext identity before decryption and before destructive SQL.

```sh
docker compose --env-file .env --profile ops run --rm backup
```

The daily systemd timer invokes `run-backup-job.sh`, requires `deployment-state/current-release.env`, verifies the running API digest and actual database schema against that authoritative state, uploads the archive, manifest and signature with `rclone --immutable`, enforces local/offsite retention, records a success timestamp and alerts on failure. The operations file is parsed as a strict allowlisted `KEY=VALUE` document; it is never executed as shell, must be a non-symlink regular file with mode `0400`/`0600`, and must be root-owned when the job runs as root. A separate hourly timer alerts when the success timestamp is stale; the monthly timer runs the isolated restore drill. Install the units from `deployment/systemd/`, copy `.env.operations.example` to a root-owned host-only `.env.operations` with mode `0600`, and validate with `BACKUP_JOB_DRY_RUN=true` before enabling timers.

The age identity, Data Protection certificate/key ring and refund HMAC keys must be backed up through a separate secret-management channel. A database backup alone is insufficient to decrypt historical sessions or refund recipient data. The production offsite target, its object-lock/immutability policy, alert delivery and restore cadence remain release gates until exercised in the selected vendor account.

### Encryption key rotation

Create a new age identity offline, retain the old identity in the recovery secret store for every retained backup encrypted to it, and change `BACKUP_AGE_RECIPIENT` plus `BACKUP_AGE_IDENTITY_FILE` together during a controlled release. Produce and restore-test a backup using the new recipient before retiring an old identity. Never overwrite an identity while backups that require it remain inside retention.

Rotate the provenance signing key independently: generate a new private/public pair offline, retain every old public key for the corresponding retained manifests, deploy the new private key only to backup creation and the public key only to restore consumers, then produce and restore-test a signed backup. Never place the private signing key beside offsite backup consumers or in the repository.

## Isolated restore drill

Never test a restore over the live database. Start the isolated restore database and select an encrypted filename under `BACKUP_DIRECTORY`:

```sh
DEPLOYMENT_ROOT="$PWD" DEPLOYMENT_ENVIRONMENT="$PWD/.env" \
  RESTORE_BACKUP_FILE=comic-management-YYYYMMDDTHHMMSSZ.dump.age \
  sh deployment/scripts/run-restore-drill-job.sh
```

The job uses a separate Compose project and internal restore-only network. Only `restore-postgres` and `restore-drill` join it; primary PostgreSQL, API and web cannot resolve the restore hostname, and no restore port is published. It requires the digest-pinned operations image from `deployment-state/current-release.env`; an explicit validated `DEPLOYMENT_RELEASE_MANIFEST` may be supplied for pre-release verification, but missing state never falls back to `.env` tags. A trap tears down containers, network and ephemeral volume after success, failure or interruption while preserving the original failure code. The restore command itself accepts only host `restore-postgres`, database `comic_management_restore`, and the exact explicit confirmation token.

The drill authenticates/decrypts the age payload and verifies the archive before recreating only `comic_management_restore`. It then restores with ownership/ACL disabled and fails unless:

- EF migration history exists;
- Wallet Paid balances reconcile with exact PaidCoinLot available/reserved totals;
- Paid/Bonus Wallet balances reconcile with immutable ledger entries;
- provider transaction and webhook delivery evidence remains linked to its fulfilled order;
- the immutable RefundRequest/recipient snapshot links to the exact original PaidCoinLot;
- RF03 Reserved, Consumed and Released reservations reconcile with transfer evidence, recipient snapshot, exact original lot, resolution operation, immutable ledger and completion/release outbox;
- Wallet/lot counters, audit and outbox evidence reconcile.

Build reproducibility limitation: release OCI digests are immutable and attested after publish, but `apt`/`apk` packages installed during image builds are not yet sourced from snapshot repositories or pinned by package version. Rebuilding the same source later may therefore produce a different digest; strict bit-for-bit reproducibility requires distro snapshots or vendored tooling.

Generic production verification reports checked row counts for provider/payment/webhook, refund/recipient, reservation, outbox and audit evidence and reconciles every Wallet against lots and immutable ledger. Separate fixture-specific CI assertions prove exact known IDs and amounts; they are not a substitute for the generic checks. Record artifact identity, signed manifest, source commit/digests, start/end time and verification output. A staging drill using retained production-like data and the configured offsite target remains mandatory evidence.

## Recovery and incident rules

- Stop unsafe fulfillment while continuing to preserve authenticated provider evidence where possible.
- Reconcile external money separately from internal order state; never infer payment from a frontend redirect.
- Restart outbox processing idempotently and inspect poison/backlog state.
- Do not repair Wallet, PaidCoinLot, refund reservation or immutable ledger rows with ad-hoc SQL. Use an approved, audited recovery procedure.
- For ambiguous refund transfer outcomes, retain the reservation and reconcile before Complete or Release.
- For paid-media leakage, revoke/rotate the provider delivery policy and account for already cached links.

Database down-migrations may be destructive or impossible. Application rollback is allowed only across compatible schema versions; otherwise restore a tested pre-release backup during a declared write outage.
