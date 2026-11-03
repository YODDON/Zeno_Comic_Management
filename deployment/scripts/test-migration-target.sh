#!/bin/sh
set -eu

entrypoint="${1:-backend/docker/migrate-entrypoint.sh}"
connection='Host=postgres;Database=comic_management;Username=test;Password=test'

if ConnectionStrings__Database="$connection" sh "$entrypoint" >/dev/null 2>&1; then
  echo "Migrator accepted a missing exact migration target." >&2
  exit 1
fi
if ConnectionStrings__Database="$connection" MIGRATION_TARGET=latest \
    sh "$entrypoint" >/dev/null 2>&1; then
  echo "Migrator accepted a mutable/non-canonical migration target." >&2
  exit 1
fi

echo "Exact migration-target fail-closed tests passed."
