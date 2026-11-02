#!/bin/sh
set -eu

environment_file="${1:-.env}"
release_manifest="${2:-}"

if [ ! -f "$environment_file" ]; then
  echo "Deployment environment file not found: $environment_file" >&2
  exit 66
fi

script_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
repository_root="$(CDPATH= cd -- "$script_directory/../.." && pwd)"
state_directory="$repository_root/deployment-state"

cd "$repository_root"
sh deployment/scripts/validate-env.sh "$environment_file"
sh deployment/scripts/validate-release-manifest.sh "$release_manifest"

compose() {
  docker compose \
    --env-file "$environment_file" \
    --env-file "$release_manifest" \
    "$@"
}

if [ "${SKIP_IMAGE_PULL:-false}" != "true" ]; then
  compose pull migrate api web backup restore-drill
fi

for manifest_key in API_IMAGE_REFERENCE WEB_IMAGE_REFERENCE MIGRATOR_IMAGE_REFERENCE OPS_IMAGE_REFERENCE; do
  reference="$(sed -n "s/^${manifest_key}=//p" "$release_manifest" | tail -n 1 | tr -d '\r')"
  docker image inspect "$reference" >/dev/null
done

compose up --detach --wait --no-build \
  postgres migrate data-protection-init api web

expected_migration="$(sed -n 's/^CURRENT_MIGRATION_ID=//p' "$release_manifest" | tail -n 1 | tr -d '\r')"
actual_migration="$(compose exec -T postgres psql \
  --username "$(sed -n 's/^POSTGRES_USER=//p' "$environment_file" | tail -n 1 | tr -d '\r')" \
  --dbname "$(sed -n 's/^POSTGRES_DB=//p' "$environment_file" | tail -n 1 | tr -d '\r')" \
  --tuples-only --no-align \
  --command 'SELECT migration_id FROM infrastructure.__ef_migrations_history ORDER BY migration_id DESC LIMIT 1;' \
  | tr -d '\r')"
if [ "$actual_migration" != "$expected_migration" ]; then
  echo "Database migration $actual_migration does not match release manifest migration $expected_migration." >&2
  exit 1
fi

base_url="${SMOKE_BASE_URL:-$(sed -n 's/^APP_ADDRESS=//p' "$environment_file" | tail -n 1 | tr -d '\r')}"
BASE_URL="$base_url" sh deployment/scripts/smoke.sh

for service in api web; do
  container_id="$(compose ps -q "$service")"
  manifest_key="$(printf '%s' "$service" | tr '[:lower:]' '[:upper:]')_IMAGE_REFERENCE"
  expected_reference="$(sed -n "s/^${manifest_key}=//p" "$release_manifest" | tail -n 1 | tr -d '\r')"
  actual_reference="$(docker inspect --format '{{.Config.Image}}' "$container_id")"
  if [ "$actual_reference" != "$expected_reference" ]; then
    echo "$service is running $actual_reference instead of $expected_reference." >&2
    exit 1
  fi
done

mkdir -p "$state_directory"
temporary_state="$(mktemp "$state_directory/.current-release.XXXXXX")"
cp "$release_manifest" "$temporary_state"
chmod 0600 "$temporary_state"
mv "$temporary_state" "$state_directory/current-release.env"
release_git_sha="$(sed -n 's/^RELEASE_GIT_SHA=//p' "$release_manifest" | tail -n 1 | tr -d '\r')"
printf 'Release %s is healthy, digest-verified and recorded.\n' "$release_git_sha"
