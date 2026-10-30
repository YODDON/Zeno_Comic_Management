#!/bin/sh
set -eu

deployment_root="${DEPLOYMENT_ROOT:-/opt/zeno-comic-management}"
deployment_environment="${DEPLOYMENT_ENVIRONMENT:-$deployment_root/.env}"
cd "$deployment_root"

backup_directory="$(sed -n 's/^BACKUP_DIRECTORY=//p' "$deployment_environment" | tail -n 1 | tr -d '\r')"
backup_directory="${backup_directory:-./backups}"
backup_file="$(find "$backup_directory" -maxdepth 1 -type f -name '*.dump.age' -print | sort | tail -n 1)"
if [ -z "$backup_file" ]; then
  echo "No local authenticated backup is available for the restore drill." >&2
  exit 66
fi

docker compose --env-file "$deployment_environment" --profile ops \
  up --detach --wait restore-postgres
docker compose --env-file "$deployment_environment" --profile ops run --rm \
  -e "BACKUP_FILE=$(basename "$backup_file")" \
  -e RESTORE_DESTRUCTIVE_CONFIRMATION=CONFIRM_ISOLATED_RESTORE_DROP \
  restore-drill
