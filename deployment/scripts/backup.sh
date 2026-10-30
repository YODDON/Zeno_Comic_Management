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

require_value DATABASE_HOST
require_value DATABASE_NAME
require_value DATABASE_USER
require_value PGPASSWORD
require_value BACKUP_AGE_RECIPIENT

timestamp="${BACKUP_TIMESTAMP:-$(date -u +%Y%m%dT%H%M%SZ)}"
archive="/backups/comic-management-${timestamp}.dump"
encrypted_archive="${archive}.age"

if [ -e "$archive" ] || [ -e "$encrypted_archive" ]; then
  echo "Refusing to overwrite an existing backup artifact." >&2
  exit 73
fi

cleanup() {
  rm -f "$archive"
}
trap cleanup EXIT HUP INT TERM

pg_dump \
  --host "$DATABASE_HOST" \
  --username "$DATABASE_USER" \
  --dbname "$DATABASE_NAME" \
  --format custom \
  --compress 9 \
  --no-owner \
  --no-privileges \
  --file "$archive"

pg_restore --list "$archive" >/dev/null

age --encrypt \
  --recipient "$BACKUP_AGE_RECIPIENT" \
  --output "$encrypted_archive" \
  "$archive"
chmod 0600 "$encrypted_archive"

echo "Encrypted PostgreSQL backup created: $(basename "$encrypted_archive")"
echo "Copy the authenticated encrypted archive to the approved immutable offsite target."
