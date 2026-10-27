# Backup, Restore, Incident Procedures

Automate encrypted offsite PostgreSQL backups with retention, alert on failure and periodically restore into isolated staging. Protect encryption keys and Data Protection key ring separately; do not include provider secrets in backups published to users. Prefer point-in-time recovery only if configured/tested. Test exact consistent recovery of Wallet ledger, lots, refund reservations, payment events and outbox.

Incident runbooks: stop unsafe fulfillment while preserving webhook evidence, reconcile provider money separately, restart outbox idempotently, inspect reconciliation discrepancies, do not issue manual wallet SQL fixes without approved audit trail. For ambiguous bank refunds: hold reservation and reconcile before retry/release. For paid media leakage: revoke/rotate delivery policy and consider previously cached links.

Release rollback must address schema compatibility; database migration rollback may be impossible without recovery backup. Test full restore, not just successful backup artifact creation.
