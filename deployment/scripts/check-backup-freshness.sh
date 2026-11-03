#!/bin/sh
set -eu

deployment_root="${DEPLOYMENT_ROOT:-/opt/zeno-comic-management}"
operations_environment="${OPERATIONS_ENVIRONMENT:-$deployment_root/.env.operations}"
status_file="${BACKUP_STATUS_DIRECTORY:-$deployment_root/deployment-state}/last-backup-success.epoch"

if [ ! -f "$operations_environment" ]; then
  echo "Operations environment file is required." >&2
  exit 66
fi
script_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
# shellcheck disable=SC1091
. "$script_directory/load-operations-config.sh"
load_operations_config "$operations_environment"

maximum_age_hours="${BACKUP_STALE_AFTER_HOURS:-36}"
case "$maximum_age_hours" in
  ''|*[!0-9]*) echo "BACKUP_STALE_AFTER_HOURS must be a positive whole number." >&2; exit 64 ;;
esac
if [ "$maximum_age_hours" -le 0 ]; then
  echo "BACKUP_STALE_AFTER_HOURS must be greater than zero." >&2
  exit 64
fi
now="$(date -u +%s)"
last_success=0
if [ -f "$status_file" ]; then
  last_success="$(cat "$status_file")"
fi
case "$last_success" in
  ''|*[!0-9]*) echo "Backup success timestamp is invalid." >&2; exit 65 ;;
esac

if [ "$last_success" -le 0 ] || \
   [ $((now - last_success)) -gt $((maximum_age_hours * 3600)) ]; then
  if [ -n "${ALERT_WEBHOOK_URL:-}" ]; then
    curl --fail --silent --show-error \
      --header 'Content-Type: application/json' \
      --data '{"text":"Comic Management backup is missing or stale."}' \
      "$ALERT_WEBHOOK_URL" >/dev/null || true
  fi
  echo "Backup freshness check failed." >&2
  exit 1
fi

echo "Backup freshness check passed."
