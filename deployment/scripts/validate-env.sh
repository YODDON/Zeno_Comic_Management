#!/bin/sh
set -eu

environment_file="${1:-.env}"
if [ ! -f "$environment_file" ]; then
  echo "Environment file not found: $environment_file" >&2
  exit 66
fi

temporary_config="$(mktemp)"
cleanup() { rm -f "$temporary_config"; }
trap cleanup EXIT HUP INT TERM
chmod 0600 "$temporary_config"

docker compose --env-file "$environment_file" config >"$temporary_config"

if grep -q 'CHANGE_ME' "$temporary_config"; then
  echo "Deployment configuration still contains CHANGE_ME placeholders." >&2
  exit 64
fi

read_value() {
  key="$1"
  sed -n "s/^${key}=//p" "$environment_file" | tail -n 1 | tr -d '\r'
}

require_environment_value() {
  key="$1"
  value="$(read_value "$key")"
  if [ -z "$value" ]; then
    echo "$key must be configured." >&2
    exit 64
  fi
}

for required_key in \
  POSTGRES_DB POSTGRES_USER POSTGRES_PASSWORD \
  GOOGLE_CLIENT_ID GOOGLE_CLIENT_SECRET \
  DATA_PROTECTION_CERTIFICATE_PASSWORD ERROR_DOCUMENTATION_BASE_URI \
  CLOUDINARY_CLOUD_NAME CLOUDINARY_API_KEY CLOUDINARY_API_SECRET \
  REFUND_FINGERPRINT_VERSION REFUND_FINGERPRINT_KEY_V1 \
  REFUND_PROTECTION_VERSION IMAGE_REGISTRY IMAGE_TAG \
  BACKUP_AGE_RECIPIENT BACKUP_AGE_IDENTITY_FILE
do
  require_environment_value "$required_key"
done

postgres_password="$(read_value POSTGRES_PASSWORD)"
if [ "${#postgres_password}" -lt 20 ]; then
  echo "Database password must contain at least 20 characters." >&2
  exit 64
fi

image_tag="$(read_value IMAGE_TAG)"
case "$image_tag" in
  latest|Latest|LATEST)
    echo "IMAGE_TAG must be immutable and cannot be latest." >&2
    exit 64
    ;;
esac
if ! printf '%s' "$image_tag" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]{6,127}$'; then
  echo "IMAGE_TAG has an invalid immutable release-tag format." >&2
  exit 64
fi

app_address="$(read_value APP_ADDRESS)"
case "$app_address" in
  http://localhost|http://127.0.0.1) ;;
  https://*) ;;
  *)
    echo "APP_ADDRESS must be HTTPS except for an explicit localhost deployment." >&2
    exit 64
    ;;
esac

certificate_file="$(read_value DATA_PROTECTION_CERTIFICATE_FILE)"
if [ -z "$certificate_file" ] || [ ! -f "$certificate_file" ]; then
  echo "DATA_PROTECTION_CERTIFICATE_FILE must identify an existing PKCS#12 file." >&2
  exit 66
fi

age_identity_file="$(read_value BACKUP_AGE_IDENTITY_FILE)"
if [ ! -f "$age_identity_file" ]; then
  echo "BACKUP_AGE_IDENTITY_FILE must identify an existing age identity file." >&2
  exit 66
fi

age_recipient="$(read_value BACKUP_AGE_RECIPIENT)"
if ! printf '%s' "$age_recipient" | grep -Eq '^age1[0-9a-z]{58}$'; then
  echo "BACKUP_AGE_RECIPIENT must be an age X25519 recipient." >&2
  exit 64
fi

fingerprint_key="$(read_value REFUND_FINGERPRINT_KEY_V1)"
decoded_key_length="$(printf '%s' "$fingerprint_key" | base64 -d 2>/dev/null | wc -c | tr -d ' ')"
if [ "$decoded_key_length" -lt 32 ]; then
  echo "REFUND_FINGERPRINT_KEY_V1 must be valid Base64 for at least 32 bytes." >&2
  exit 64
fi

sepay_enabled="$(read_value SEPAY_WEBHOOK_ENABLED)"
if [ "$sepay_enabled" = "true" ]; then
  if [ -z "$(read_value SEPAY_WEBHOOK_SECRET)" ] || \
     [ -z "$(read_value SEPAY_EXPECTED_ACCOUNT_NUMBER)" ]; then
    echo "Enabled SePay delivery requires its secret and expected account." >&2
    exit 64
  fi
  echo "WARNING: enabling SePay is not proof that the project-account Q02 gate passed." >&2
fi

echo "Deployment environment structure is valid; provider verification remains a separate gate."
