#!/bin/sh
set -eu

environment_file="${1:-.env}"
release_tag="${2:-}"

case "$release_tag" in
  latest|Latest|LATEST|"")
    echo "An immutable release tag is required; latest is forbidden." >&2
    exit 64
    ;;
esac
if ! printf '%s' "$release_tag" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]{6,127}$'; then
  echo "Release tag format is invalid." >&2
  exit 64
fi

if [ ! -f "$environment_file" ]; then
  echo "Deployment environment file not found: $environment_file" >&2
  exit 66
fi

script_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
repository_root="$(CDPATH= cd -- "$script_directory/../.." && pwd)"
state_directory="$repository_root/deployment-state"
temporary_tag_file="$(mktemp)"
cleanup() { rm -f "$temporary_tag_file"; }
trap cleanup EXIT HUP INT TERM
printf 'IMAGE_TAG=%s\n' "$release_tag" >"$temporary_tag_file"
chmod 0600 "$temporary_tag_file"

cd "$repository_root"
sh deployment/scripts/validate-env.sh "$environment_file"

compose() {
  docker compose \
    --env-file "$environment_file" \
    --env-file "$temporary_tag_file" \
    "$@"
}

if [ "${SKIP_IMAGE_PULL:-false}" != "true" ]; then
  compose pull migrate api web backup restore-drill
fi

compose up --detach --wait --no-build \
  postgres migrate data-protection-init api web

base_url="${SMOKE_BASE_URL:-$(sed -n 's/^APP_ADDRESS=//p' "$environment_file" | tail -n 1 | tr -d '\r')}"
BASE_URL="$base_url" sh deployment/scripts/smoke.sh

mkdir -p "$state_directory"
printf '%s\n' "$release_tag" >"$state_directory/current-image-tag"
chmod 0600 "$state_directory/current-image-tag"
printf 'Release %s is healthy and recorded.\n' "$release_tag"
