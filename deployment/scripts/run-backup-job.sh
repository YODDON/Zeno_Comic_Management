#!/bin/sh
set -eu
umask 077

deployment_root="${DEPLOYMENT_ROOT:-/opt/zeno-comic-management}"
deployment_environment="${DEPLOYMENT_ENVIRONMENT:-$deployment_root/.env}"
operations_environment="${OPERATIONS_ENVIRONMENT:-$deployment_root/.env.operations}"
status_directory="${BACKUP_STATUS_DIRECTORY:-$deployment_root/deployment-state}"

if [ ! -f "$deployment_environment" ] || [ ! -f "$operations_environment" ]; then
  echo "Deployment and operations environment files are required." >&2
  exit 66
fi

script_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
# shellcheck disable=SC1091
. "$script_directory/load-operations-config.sh"
load_operations_config "$operations_environment"

require_value() {
  variable_name="$1"
  eval "variable_value=\${$variable_name:-}"
  if [ -z "$variable_value" ]; then
    echo "$variable_name is required." >&2
    exit 64
  fi
}

require_value OFFSITE_REMOTE
require_value LOCAL_RETENTION_DAYS
require_value OFFSITE_RETENTION_DAYS

case "$LOCAL_RETENTION_DAYS:$OFFSITE_RETENTION_DAYS" in
  *[!0-9:]*|:*|*:) echo "Retention values must be positive whole days." >&2; exit 64 ;;
esac
if [ "$LOCAL_RETENTION_DAYS" -le 0 ] || [ "$OFFSITE_RETENTION_DAYS" -le 0 ]; then
  echo "Retention values must be greater than zero." >&2
  exit 64
fi

if [ "${BACKUP_JOB_DRY_RUN:-false}" = "true" ]; then
  echo "Backup job dry-run passed: encrypted age backup, retention and offsite path are configured."
  exit 0
fi

notify_failure() {
  if [ -n "${ALERT_WEBHOOK_URL:-}" ]; then
    curl --fail --silent --show-error \
      --header 'Content-Type: application/json' \
      --data '{"text":"Comic Management backup job failed; inspect the VPS journal."}' \
      "$ALERT_WEBHOOK_URL" >/dev/null || true
  fi
}
trap notify_failure EXIT HUP INT TERM

cd "$deployment_root"
active_release="${DEPLOYMENT_RELEASE_MANIFEST:-$deployment_root/deployment-state/current-release.env}"
if [ ! -f "$active_release" ]; then
  echo "Authoritative active release state is required for scheduled backup." >&2
  exit 66
fi
sh deployment/scripts/validate-release-manifest.sh "$active_release"

compose() {
  docker compose --env-file "$deployment_environment" \
    --env-file "$active_release" "$@"
}

api_container="$(compose ps -q api)"
if [ -z "$api_container" ]; then
  echo "The active API container is not running; backup provenance cannot be established." >&2
  exit 1
fi
expected_api_reference="$(sed -n 's/^API_IMAGE_REFERENCE=//p' "$active_release" | tail -n 1 | tr -d '\r')"
actual_api_reference="$(docker inspect --format '{{.Config.Image}}' "$api_container")"
if [ "$actual_api_reference" != "$expected_api_reference" ]; then
  echo "The running API image does not match authoritative active release state." >&2
  exit 1
fi

database_user="$(sed -n 's/^POSTGRES_USER=//p' "$deployment_environment" | tail -n 1 | tr -d '\r')"
database_name="$(sed -n 's/^POSTGRES_DB=//p' "$deployment_environment" | tail -n 1 | tr -d '\r')"
actual_schema_id="$(compose exec -T postgres psql \
  --username "$database_user" --dbname "$database_name" \
  --tuples-only --no-align \
  --command 'SELECT "MigrationId" FROM infrastructure.__ef_migrations_history ORDER BY "MigrationId" DESC LIMIT 1;' \
  | tr -d '\r')"
recorded_schema_id="$(sed -n 's/^ACTIVE_SCHEMA_ID=//p' "$active_release" | tail -n 1 | tr -d '\r')"
if [ -z "$actual_schema_id" ] || [ "$actual_schema_id" != "$recorded_schema_id" ]; then
  echo "The running database schema does not match authoritative active release state." >&2
  exit 1
fi

compose --profile ops run --rm \
  -e "BACKUP_MIGRATION_ID=$actual_schema_id" backup

backup_directory="$(sed -n 's/^BACKUP_DIRECTORY=//p' "$deployment_environment" | tail -n 1 | tr -d '\r')"
backup_directory="${backup_directory:-./backups}"
newest_backup="$(find "$backup_directory" -maxdepth 1 -type f -name '*.dump.age' -print | sort | tail -n 1)"
if [ -z "$newest_backup" ]; then
  echo "No authenticated encrypted backup was produced." >&2
  exit 1
fi
newest_manifest="${newest_backup}.manifest"
newest_signature="${newest_manifest}.sig"
if [ ! -f "$newest_manifest" ] || [ ! -f "$newest_signature" ]; then
  echo "The backup provenance manifest/signature was not produced." >&2
  exit 1
fi

for artifact in "$newest_backup" "$newest_manifest" "$newest_signature"; do
  rclone copyto "$artifact" "$OFFSITE_REMOTE/$(basename "$artifact")" \
    --immutable
done
rclone delete "$OFFSITE_REMOTE" \
  --include '*.dump.age' \
  --include '*.dump.age.manifest' \
  --include '*.dump.age.manifest.sig' \
  --min-age "${OFFSITE_RETENTION_DAYS}d"
find "$backup_directory" -maxdepth 1 -type f \
  \( -name '*.dump.age' -o -name '*.dump.age.manifest' -o \
     -name '*.dump.age.manifest.sig' \) \
  -mtime "+$LOCAL_RETENTION_DAYS" -delete

mkdir -p "$status_directory"
date -u +%s >"$status_directory/last-backup-success.epoch.tmp"
mv "$status_directory/last-backup-success.epoch.tmp" \
  "$status_directory/last-backup-success.epoch"
trap - EXIT HUP INT TERM
echo "Backup uploaded offsite and retention enforced: $(basename "$newest_backup")"
