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

case "$BACKUP_FILE" in
  */*|*\\*|.|..) echo "BACKUP_FILE must be a filename under /backups." >&2; exit 64 ;;
esac

encrypted_archive="/backups/$BACKUP_FILE"
manifest="${encrypted_archive}.manifest"
signature="${manifest}.sig"
provenance_public_key=/run/secrets/backup_provenance_public_key
temporary_archive="/tmp/restore-$$.dump"
cleanup() { rm -f "$temporary_archive"; }
trap cleanup EXIT HUP INT TERM

if [ ! -f "$encrypted_archive" ]; then
  echo "The authenticated encrypted backup is required." >&2
  exit 66
fi
if [ ! -f "$manifest" ] || [ ! -f "$signature" ] || \
   [ ! -r "$provenance_public_key" ]; then
  echo "The signed backup provenance bundle is required." >&2
  exit 66
fi

openssl dgst -sha256 \
  -verify "$provenance_public_key" \
  -signature "$signature" \
  "$manifest" >/dev/null

manifest_value() {
  manifest_key="$1"
  manifest_count="$(grep -c "^${manifest_key}=" "$manifest" || true)"
  if [ "$manifest_count" -ne 1 ]; then
    echo "Backup provenance field is missing or duplicated: $manifest_key" >&2
    exit 65
  fi
  sed -n "s/^${manifest_key}=//p" "$manifest"
}

if [ "$(manifest_value format_version)" != "1" ] || \
   [ "$(manifest_value artifact)" != "$BACKUP_FILE" ] || \
   [ "$(manifest_value backup_format)" != "postgresql-custom-age-v1" ]; then
  echo "Backup provenance format or artifact identity is invalid." >&2
  exit 65
fi

expected_sha256="$(manifest_value ciphertext_sha256)"
actual_sha256="$(sha256sum "$encrypted_archive" | awk '{print $1}')"
if [ "$expected_sha256" != "$actual_sha256" ]; then
  echo "Backup ciphertext does not match its signed provenance manifest." >&2
  exit 65
fi

for required_manifest_key in \
    source_environment database git_sha release_created_at api_image_digest web_image_digest \
  migrator_image_digest migration_id created_at
do
  if [ -z "$(manifest_value "$required_manifest_key")" ]; then
    echo "Backup provenance field is empty: $required_manifest_key" >&2
    exit 65
  fi
done

for digest_key in api_image_digest web_image_digest migrator_image_digest; do
  if ! manifest_value "$digest_key" | grep -Eq '^sha256:[0-9a-f]{64}$'; then
    echo "Backup provenance image digest is invalid: $digest_key" >&2
    exit 65
  fi
done

if ! manifest_value git_sha | grep -Eq '^[0-9a-f]{40}([0-9a-f]{24})?$'; then
  echo "Backup provenance Git SHA is invalid." >&2
  exit 65
fi
if ! manifest_value created_at | grep -Eq '^[0-9]{8}T[0-9]{6}Z$'; then
  echo "Backup provenance creation timestamp is invalid." >&2
  exit 65
fi
if ! manifest_value release_created_at | grep -Eq \
  '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'; then
  echo "Backup provenance release timestamp is invalid." >&2
  exit 65
fi

age --decrypt \
  --identity /run/secrets/backup_age_identity \
  --output "$temporary_archive" \
  "$encrypted_archive"
pg_restore --list "$temporary_archive" >/dev/null

/opt/comic-management/restore-target-guard.sh \
  "$TARGET_DATABASE_HOST" \
  "$TARGET_DATABASE_NAME" \
  "$RESTORE_DESTRUCTIVE_CONFIRMATION"

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
