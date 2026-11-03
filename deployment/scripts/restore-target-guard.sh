#!/bin/sh
set -eu

target_host="${1:-}"
target_database="${2:-}"
confirmation="${3:-}"

allowed_host="restore-postgres"
allowed_database="comic_management_restore"
required_confirmation="CONFIRM_ISOLATED_RESTORE_DROP"

if [ "$target_host" != "$allowed_host" ]; then
  echo "Restore rejected: target host is not the isolated restore service." >&2
  exit 64
fi

if [ "$target_database" != "$allowed_database" ]; then
  echo "Restore rejected: target database is not the isolated restore database." >&2
  exit 64
fi

if [ "$confirmation" != "$required_confirmation" ]; then
  echo "Restore rejected: explicit destructive confirmation is missing or invalid." >&2
  exit 64
fi

printf 'Validated isolated restore target: host=%s database=%s\n' \
  "$allowed_host" "$allowed_database"
