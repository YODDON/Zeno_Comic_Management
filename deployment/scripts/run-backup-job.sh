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

# shellcheck disable=SC1090
. "$operations_environment"

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
docker compose --env-file "$deployment_environment" --profile ops run --rm backup

backup_directory="$(sed -n 's/^BACKUP_DIRECTORY=//p' "$deployment_environment" | tail -n 1 | tr -d '\r')"
backup_directory="${backup_directory:-./backups}"
newest_backup="$(find "$backup_directory" -maxdepth 1 -type f -name '*.dump.age' -print | sort | tail -n 1)"
if [ -z "$newest_backup" ]; then
  echo "No authenticated encrypted backup was produced." >&2
  exit 1
fi

rclone copyto "$newest_backup" "$OFFSITE_REMOTE/$(basename "$newest_backup")" \
  --immutable
rclone delete "$OFFSITE_REMOTE" \
  --include '*.dump.age' \
  --min-age "${OFFSITE_RETENTION_DAYS}d"
find "$backup_directory" -maxdepth 1 -type f -name '*.dump.age' \
  -mtime "+$LOCAL_RETENTION_DAYS" -delete

mkdir -p "$status_directory"
date -u +%s >"$status_directory/last-backup-success.epoch.tmp"
mv "$status_directory/last-backup-success.epoch.tmp" \
  "$status_directory/last-backup-success.epoch"
trap - EXIT HUP INT TERM
echo "Backup uploaded offsite and retention enforced: $(basename "$newest_backup")"
