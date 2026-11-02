#!/bin/sh
set -eu

deployment_root="${DEPLOYMENT_ROOT:-/opt/zeno-comic-management}"
deployment_environment="${DEPLOYMENT_ENVIRONMENT:-$deployment_root/.env}"
restore_project="${RESTORE_COMPOSE_PROJECT:-zeno-comic-management-restore-drill}"
cd "$deployment_root"

restore_compose() {
  docker compose -p "$restore_project" \
    --env-file "$deployment_environment" --profile ops "$@"
}

cleanup() {
  original_status=$?
  trap - EXIT HUP INT TERM
  set +e
  restore_compose down --volumes --remove-orphans >/dev/null 2>&1
  cleanup_status=$?
  set -e
  if [ "$original_status" -ne 0 ]; then
    exit "$original_status"
  fi
  if [ "$cleanup_status" -ne 0 ]; then
    echo "Restore drill passed but isolated cleanup failed." >&2
    exit "$cleanup_status"
  fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

backup_directory="$(sed -n 's/^BACKUP_DIRECTORY=//p' "$deployment_environment" | tail -n 1 | tr -d '\r')"
backup_directory="${backup_directory:-./backups}"
if [ -n "${RESTORE_BACKUP_FILE:-}" ]; then
  case "$RESTORE_BACKUP_FILE" in
    */*|*\\*|.|..) echo "RESTORE_BACKUP_FILE must be a filename under BACKUP_DIRECTORY." >&2; exit 64 ;;
  esac
  backup_file="$backup_directory/$RESTORE_BACKUP_FILE"
else
  backup_file="$(find "$backup_directory" -maxdepth 1 -type f -name '*.dump.age' -print | sort | tail -n 1)"
fi
if [ -z "$backup_file" ] || [ ! -f "$backup_file" ]; then
  echo "No local authenticated backup is available for the restore drill." >&2
  exit 66
fi

restore_compose up --detach --wait restore-postgres
restore_compose run --rm \
  -e "BACKUP_FILE=$(basename "$backup_file")" \
  -e RESTORE_DESTRUCTIVE_CONFIRMATION=CONFIRM_ISOLATED_RESTORE_DROP \
  restore-drill
