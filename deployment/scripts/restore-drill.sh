#!/bin/sh
set -eu
umask 077

require_value() {
  variable_name="$1"
  eval "variable_value=\${$variable_name:-}"
  if [ -z "$variable_value" ]; then
    echo "$variable_name is required." >&2
    exit 64
  fi
}

require_value TARGET_DATABASE_HOST
require_value TARGET_DATABASE_NAME
require_value DATABASE_USER
require_value PGPASSWORD
require_value BACKUP_FILE
require_value RESTORE_DESTRUCTIVE_CONFIRMATION

/opt/comic-management/restore-target-guard.sh \
  "$TARGET_DATABASE_HOST" \
  "$TARGET_DATABASE_NAME" \
  "$RESTORE_DESTRUCTIVE_CONFIRMATION"

case "$BACKUP_FILE" in
  */*|*\\*|.|..) echo "BACKUP_FILE must be a filename under /backups." >&2; exit 64 ;;
esac

encrypted_archive="/backups/$BACKUP_FILE"
temporary_archive="/tmp/restore-$$.dump"
cleanup() { rm -f "$temporary_archive"; }
trap cleanup EXIT HUP INT TERM

if [ ! -f "$encrypted_archive" ]; then
  echo "The authenticated encrypted backup is required." >&2
  exit 66
fi

age --decrypt \
  --identity /run/secrets/backup_age_identity \
  --output "$temporary_archive" \
  "$encrypted_archive"
pg_restore --list "$temporary_archive" >/dev/null

psql --host "$TARGET_DATABASE_HOST" --username "$DATABASE_USER" --dbname postgres \
  --set ON_ERROR_STOP=1 \
  --command "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$TARGET_DATABASE_NAME' AND pid <> pg_backend_pid();" \
  --command "DROP DATABASE IF EXISTS \"$TARGET_DATABASE_NAME\";" \
  --command "CREATE DATABASE \"$TARGET_DATABASE_NAME\";"

pg_restore \
  --host "$TARGET_DATABASE_HOST" \
  --username "$DATABASE_USER" \
  --dbname "$TARGET_DATABASE_NAME" \
  --exit-on-error \
  --no-owner \
  --no-privileges \
  "$temporary_archive"

psql --host "$TARGET_DATABASE_HOST" --username "$DATABASE_USER" \
  --dbname "$TARGET_DATABASE_NAME" --set ON_ERROR_STOP=1 \
  --file /opt/comic-management/recovery-verification.sql

echo "Restore drill and financial reconciliation checks passed for $BACKUP_FILE."
