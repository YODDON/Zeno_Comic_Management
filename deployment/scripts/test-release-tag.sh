#!/bin/sh
set -eu

script="${1:-deployment/scripts/validate-release-manifest.sh}"
temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT HUP INT TERM
digest="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
cat >"$temporary_directory/release.env" <<EOF
RELEASE_GIT_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
RELEASE_CREATED_AT=2026-11-02T00:00:00Z
CURRENT_MIGRATION_ID=20261031120000_D01
API_IMAGE_REFERENCE=registry.example/api@$digest
API_IMAGE_DIGEST=$digest
WEB_IMAGE_REFERENCE=registry.example/web@$digest
WEB_IMAGE_DIGEST=$digest
MIGRATOR_IMAGE_REFERENCE=registry.example/migrator@$digest
MIGRATOR_IMAGE_DIGEST=$digest
OPS_IMAGE_REFERENCE=registry.example/ops@$digest
OPS_IMAGE_DIGEST=$digest
EOF

sh "$script" "$temporary_directory/release.env" >/dev/null
sed 's#API_IMAGE_REFERENCE=.*#API_IMAGE_REFERENCE=registry.example/api:latest#' \
  "$temporary_directory/release.env" >"$temporary_directory/latest.env"
if sh "$script" "$temporary_directory/latest.env" >/dev/null 2>&1; then
  echo 'latest image reference unexpectedly passed release validation.' >&2
  exit 1
fi
sed 's/^API_IMAGE_DIGEST=.*/API_IMAGE_DIGEST=sha256:bad/' \
  "$temporary_directory/release.env" >"$temporary_directory/bad-digest.env"
if sh "$script" "$temporary_directory/bad-digest.env" >/dev/null 2>&1; then
  echo 'invalid digest unexpectedly passed release validation.' >&2
  exit 1
fi

echo 'Digest-pinned release manifest validation passed.'
