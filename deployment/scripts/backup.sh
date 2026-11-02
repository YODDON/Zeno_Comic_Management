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
require_value BACKUP_SOURCE_ENVIRONMENT
require_value BACKUP_GIT_SHA
require_value BACKUP_API_IMAGE_DIGEST
require_value BACKUP_WEB_IMAGE_DIGEST
require_value BACKUP_MIGRATOR_IMAGE_DIGEST
require_value BACKUP_MIGRATION_ID

provenance_private_key=/run/secrets/backup_provenance_private_key
if [ ! -r "$provenance_private_key" ]; then
  echo "The backup provenance private key is required." >&2
  exit 66
fi

for digest_name in BACKUP_API_IMAGE_DIGEST BACKUP_WEB_IMAGE_DIGEST BACKUP_MIGRATOR_IMAGE_DIGEST; do
  eval "digest_value=\${$digest_name}"
  if ! printf '%s' "$digest_value" | grep -Eq '^sha256:[0-9a-f]{64}$'; then
    echo "$digest_name must be a sha256 image digest." >&2
    exit 64
  fi
done
if ! printf '%s' "$BACKUP_GIT_SHA" | grep -Eq '^[0-9a-f]{40}([0-9a-f]{24})?$'; then
  echo "BACKUP_GIT_SHA must be a full lowercase Git object ID." >&2
  exit 64
fi
for metadata_value in "$BACKUP_SOURCE_ENVIRONMENT" "$DATABASE_NAME" "$BACKUP_MIGRATION_ID"; do
  if ! printf '%s' "$metadata_value" | grep -Eq '^[A-Za-z0-9_.-]{1,200}$'; then
    echo "Backup provenance metadata contains an invalid identifier." >&2
    exit 64
  fi
done

timestamp="${BACKUP_TIMESTAMP:-$(date -u +%Y%m%dT%H%M%SZ)}"
archive="/backups/comic-management-${timestamp}.dump"
encrypted_archive="${archive}.age"
manifest="${encrypted_archive}.manifest"
signature="${manifest}.sig"

if [ -e "$archive" ] || [ -e "$encrypted_archive" ] || \
   [ -e "$manifest" ] || [ -e "$signature" ]; then
  echo "Refusing to overwrite an existing backup artifact." >&2
  exit 73
fi

cleanup() {
  rm -f "$archive"
  if [ "${backup_complete:-0}" -ne 1 ]; then
    rm -f "$encrypted_archive" "$manifest" "$signature"
  fi
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

ciphertext_sha256="$(sha256sum "$encrypted_archive" | awk '{print $1}')"
cat >"$manifest" <<EOF
format_version=1
artifact=$(basename "$encrypted_archive")
ciphertext_sha256=$ciphertext_sha256
source_environment=$BACKUP_SOURCE_ENVIRONMENT
database=$DATABASE_NAME
git_sha=$BACKUP_GIT_SHA
api_image_digest=$BACKUP_API_IMAGE_DIGEST
web_image_digest=$BACKUP_WEB_IMAGE_DIGEST
migrator_image_digest=$BACKUP_MIGRATOR_IMAGE_DIGEST
migration_id=$BACKUP_MIGRATION_ID
created_at=$timestamp
backup_format=postgresql-custom-age-v1
EOF

openssl dgst -sha256 \
  -sign "$provenance_private_key" \
  -out "$signature" \
  "$manifest"

chmod 0644 "$encrypted_archive" "$manifest" "$signature"
backup_complete=1

echo "Encrypted PostgreSQL backup created: $(basename "$encrypted_archive")"
echo "Signed provenance manifest created: $(basename "$manifest")"
echo "Copy the encrypted archive, signed manifest and signature to the approved immutable offsite target."
