#!/bin/sh
set -eu

guard="${1:-deployment/scripts/restore-target-guard.sh}"
confirmation="CONFIRM_ISOLATED_RESTORE_DROP"

expect_rejected() {
  if sh "$guard" "$1" "$2" "$3" >/dev/null 2>&1; then
    echo "Unsafe restore target was accepted: host=$1 database=$2" >&2
    exit 1
  fi
}

sh "$guard" restore-postgres comic_management_restore "$confirmation" >/dev/null
expect_rejected restore-postgres comic_management "$confirmation"
expect_rejected postgres comic_management_restore "$confirmation"
expect_rejected localhost comic_management_restore "$confirmation"
expect_rejected restore-postgres comic_management_restore ""
expect_rejected postgres comic_management "$confirmation"

echo "Restore target fail-closed tests passed."
