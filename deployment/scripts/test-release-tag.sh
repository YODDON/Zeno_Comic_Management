#!/bin/sh
set -eu

script="${1:-deployment/scripts/switch-release.sh}"

expect_rejected() {
  set +e
  sh "$script" /missing-environment "$1" >/dev/null 2>&1
  status=$?
  set -e
  if [ "$status" -ne 64 ]; then
    echo "Invalid release tag was accepted: $1" >&2
    exit 1
  fi
}

expect_rejected ""
expect_rejected latest
expect_rejected bad
expect_rejected 'bad/tag'

# A valid tag reaches environment-file validation rather than tag rejection.
set +e
sh "$script" /missing-environment 0123456789abcdef >/dev/null 2>&1
status=$?
set -e
if [ "$status" -ne 66 ]; then
  echo "Missing environment unexpectedly succeeded." >&2
  exit 1
fi

echo "Immutable release-tag validation tests passed."
