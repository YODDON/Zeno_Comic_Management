#!/bin/sh
set -eu

if [ -z "${ConnectionStrings__Database:-}" ]; then
  echo "ConnectionStrings__Database is required for migrations." >&2
  exit 64
fi

exec /app/efbundle --connection "$ConnectionStrings__Database"
