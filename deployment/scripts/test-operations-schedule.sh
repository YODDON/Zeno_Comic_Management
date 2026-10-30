#!/bin/sh
set -eu

assert_contains() {
  file="$1"
  expected="$2"
  if ! grep -Fq "$expected" "$file"; then
    echo "Expected '$expected' in $file." >&2
    exit 1
  fi
}

assert_contains deployment/systemd/zeno-backup.timer 'OnCalendar=*-*-* 02:15:00 UTC'
assert_contains deployment/systemd/zeno-backup.timer 'Persistent=true'
assert_contains deployment/systemd/zeno-backup-freshness.timer 'OnCalendar=hourly'
assert_contains deployment/systemd/zeno-backup-freshness.timer 'Persistent=true'
assert_contains deployment/systemd/zeno-restore-drill.timer 'OnCalendar=monthly'
assert_contains deployment/systemd/zeno-restore-drill.timer 'Persistent=true'
assert_contains deployment/systemd/zeno-backup.service \
  'ExecStart=/bin/sh /opt/zeno-comic-management/deployment/scripts/run-backup-job.sh'
assert_contains deployment/systemd/zeno-backup-freshness.service \
  'ExecStart=/bin/sh /opt/zeno-comic-management/deployment/scripts/check-backup-freshness.sh'
assert_contains deployment/systemd/zeno-restore-drill.service \
  'ExecStart=/bin/sh /opt/zeno-comic-management/deployment/scripts/run-restore-drill-job.sh'

temporary_directory="$(mktemp -d)"
cleanup() { rm -rf "$temporary_directory"; }
trap cleanup EXIT HUP INT TERM
printf 'BACKUP_STALE_AFTER_HOURS=36\nALERT_WEBHOOK_URL=\n' \
  >"$temporary_directory/operations.env"
mkdir -p "$temporary_directory/state"
date -u +%s >"$temporary_directory/state/last-backup-success.epoch"
DEPLOYMENT_ROOT="$temporary_directory" \
  OPERATIONS_ENVIRONMENT="$temporary_directory/operations.env" \
  BACKUP_STATUS_DIRECTORY="$temporary_directory/state" \
  sh deployment/scripts/check-backup-freshness.sh >/dev/null

printf '1\n' >"$temporary_directory/state/last-backup-success.epoch"
if DEPLOYMENT_ROOT="$temporary_directory" \
    OPERATIONS_ENVIRONMENT="$temporary_directory/operations.env" \
    BACKUP_STATUS_DIRECTORY="$temporary_directory/state" \
    sh deployment/scripts/check-backup-freshness.sh >/dev/null 2>&1; then
  echo 'A stale backup success timestamp was accepted.' >&2
  exit 1
fi

echo 'Backup, stale-alert and restore-drill schedule definitions passed.'
