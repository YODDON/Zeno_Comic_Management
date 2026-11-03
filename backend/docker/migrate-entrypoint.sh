#!/bin/sh
set -eu

if [ -z "${ConnectionStrings__Database:-}" ]; then
  echo "ConnectionStrings__Database is required for migrations." >&2
  exit 64
fi

if [ -z "${MIGRATION_TARGET:-}" ]; then
  echo "MIGRATION_TARGET is required; release manifests must pin the exact schema target." >&2
  exit 64
fi
if ! printf '%s' "$MIGRATION_TARGET" | grep -Eq '^[0-9]{14}_[A-Za-z0-9_]+$'; then
  echo "MIGRATION_TARGET has an invalid EF migration identifier." >&2
  exit 64
fi

exec /app/efbundle "$MIGRATION_TARGET" --connection "$ConnectionStrings__Database"
