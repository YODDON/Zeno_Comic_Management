# Backup, Restore and Incident Procedures

## Encrypted backup

The `ops` profile runs `pg_dump` in custom format, verifies its catalog and encrypts it with authenticated `age` X25519 encryption. Plaintext dumps are removed on every exit path. Authentication is intrinsic to the age payload: a modified header or ciphertext, or the wrong identity, fails before PostgreSQL is touched.

```sh
docker compose --env-file .env --profile ops run --rm backup
```

The daily systemd timer invokes `run-backup-job.sh`, uploads `*.dump.age` with `rclone --immutable`, enforces local/offsite retention, records a success timestamp and alerts on failure. A separate hourly timer alerts when that timestamp is stale; the monthly timer runs the isolated restore drill. Install the units from `deployment/systemd/`, copy `.env.operations.example` to a host-only `.env.operations`, and validate with `BACKUP_JOB_DRY_RUN=true` before enabling timers.

The age identity, Data Protection certificate/key ring and refund HMAC keys must be backed up through a separate secret-management channel. A database backup alone is insufficient to decrypt historical sessions or refund recipient data. The production offsite target, its object-lock/immutability policy, alert delivery and restore cadence remain release gates until exercised in the selected vendor account.

### Encryption key rotation

Create a new age identity offline, retain the old identity in the recovery secret store for every retained backup encrypted to it, and change `BACKUP_AGE_RECIPIENT` plus `BACKUP_AGE_IDENTITY_FILE` together during a controlled release. Produce and restore-test a backup using the new recipient before retiring an old identity. Never overwrite an identity while backups that require it remain inside retention.

## Isolated restore drill

Never test a restore over the live database. Start the isolated restore database and select an encrypted filename under `BACKUP_DIRECTORY`:

```sh
docker compose --env-file .env --profile ops up --detach --wait restore-postgres
BACKUP_FILE=comic-management-YYYYMMDDTHHMMSSZ.dump.age \
RESTORE_DESTRUCTIVE_CONFIRMATION=CONFIRM_ISOLATED_RESTORE_DROP \
  docker compose --env-file .env --profile ops run --rm restore-drill
```

The restore command is fail-closed: the script accepts only host `restore-postgres`, database `comic_management_restore`, and the exact explicit confirmation token. `localhost`, the primary service/database and arbitrary targets remain forbidden even when the token is supplied. It prints only the sanitized allowlisted target.

The drill authenticates/decrypts the age payload and verifies the archive before recreating only `comic_management_restore`. It then restores with ownership/ACL disabled and fails unless:

- EF migration history exists;
- Wallet Paid balances reconcile with exact PaidCoinLot available/reserved totals;
- Paid/Bonus Wallet balances reconcile with immutable ledger entries;
- provider transaction and webhook delivery evidence remains linked to its fulfilled order;
- the immutable RefundRequest/recipient snapshot links to the exact original PaidCoinLot;
- active refund reservations, Wallet/lot counters, immutable ledger, audit and outbox evidence reconcile.

Record the artifact identity, source commit/image tag, start/end time and verification output. The automated D01 fixture covers the high-risk financial graph, but a staging drill using retained production-like data and the configured offsite target remains mandatory evidence.

## Recovery and incident rules

- Stop unsafe fulfillment while continuing to preserve authenticated provider evidence where possible.
- Reconcile external money separately from internal order state; never infer payment from a frontend redirect.
- Restart outbox processing idempotently and inspect poison/backlog state.
- Do not repair Wallet, PaidCoinLot, refund reservation or immutable ledger rows with ad-hoc SQL. Use an approved, audited recovery procedure.
- For ambiguous refund transfer outcomes, retain the reservation and reconcile before Complete or Release.
- For paid-media leakage, revoke/rotate the provider delivery policy and account for already cached links.

Database down-migrations may be destructive or impossible. Application rollback is allowed only across compatible schema versions; otherwise restore a tested pre-release backup during a declared write outage.
