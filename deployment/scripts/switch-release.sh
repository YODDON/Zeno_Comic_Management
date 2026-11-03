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

manifest_value() {
  key="$1"
  file="$2"
  sed -n "s/^${key}=//p" "$file" | tail -n 1 | tr -d '\r'
}

schema_is_compatible() {
  schema_id="$1"
  compatibility_list="$2"
  case ",$compatibility_list," in
    *",$schema_id,"*) return 0 ;;
    *) return 1 ;;
  esac
}

target_migration="$(manifest_value CURRENT_MIGRATION_ID "$release_manifest")"
target_compatibility="$(manifest_value COMPATIBLE_SCHEMA_IDS "$release_manifest")"
active_release="$state_directory/current-release.env"
preflight_schema=''
migration_execution_target="$target_migration"
is_application_rollback=false

if [ -f "$active_release" ]; then
  sh deployment/scripts/validate-release-manifest.sh "$active_release"
  active_compose() {
    docker compose --env-file "$environment_file" --env-file "$active_release" "$@"
  }
  active_api_container="$(active_compose ps -q api)"
  if [ -z "$active_api_container" ]; then
    echo "Active release state exists but its API workload is not running." >&2
    exit 1
  fi
  expected_active_api="$(manifest_value API_IMAGE_REFERENCE "$active_release")"
  actual_active_api="$(docker inspect --format '{{.Config.Image}}' "$active_api_container")"
  if [ "$actual_active_api" != "$expected_active_api" ]; then
    echo "Running API image does not match active release state." >&2
    exit 1
  fi
  preflight_schema="$(active_compose exec -T postgres psql \
    --username "$(manifest_value POSTGRES_USER "$environment_file")" \
    --dbname "$(manifest_value POSTGRES_DB "$environment_file")" \
    --tuples-only --no-align \
    --command 'SELECT "MigrationId" FROM infrastructure.__ef_migrations_history ORDER BY "MigrationId" DESC LIMIT 1;' \
    | tr -d '\r')"
  recorded_schema="$(manifest_value ACTIVE_SCHEMA_ID "$active_release")"
  if [ -z "$recorded_schema" ] || [ "$recorded_schema" != "$preflight_schema" ]; then
    echo "Active release state does not match the running database schema." >&2
    exit 1
  fi

  current_sequence="${preflight_schema%%_*}"
  target_sequence="${target_migration%%_*}"
  if [ "$target_sequence" -lt "$current_sequence" ] && \
     ! schema_is_compatible "$preflight_schema" "$target_compatibility"; then
    echo "Target release is not compatible with current schema $preflight_schema; refusing rollback before image pull or workload switch." >&2
    exit 1
  fi
  if [ "$target_sequence" -lt "$current_sequence" ]; then
    # A compatible application rollback is forward-schema-only. The older
    # release bundle cannot know a future migration ID, so never start it.
    # Keep the already-running database/schema and switch only application
    # workloads after the compatibility declaration has passed.
    is_application_rollback=true
  fi
else
  if docker volume inspect zeno-comic-management_postgres_data >/dev/null 2>&1 || \
     [ -n "$(docker compose --env-file "$environment_file" ps -q postgres 2>/dev/null || true)" ]; then
    echo "Database state exists without authoritative active release state; refusing deployment." >&2
    exit 1
  fi
fi

if [ "${SKIP_IMAGE_PULL:-false}" != "true" ]; then
  compose pull migrate api web backup restore-drill
fi

for manifest_key in API_IMAGE_REFERENCE WEB_IMAGE_REFERENCE MIGRATOR_IMAGE_REFERENCE OPS_IMAGE_REFERENCE; do
  reference="$(sed -n "s/^${manifest_key}=//p" "$release_manifest" | tail -n 1 | tr -d '\r')"
  docker image inspect "$reference" >/dev/null
done

if [ "$is_application_rollback" = "true" ]; then
  compose up --detach --wait --no-build --no-deps api web
else
  export MIGRATION_TARGET="$migration_execution_target"
  compose up --detach --wait --no-build \
    postgres migrate data-protection-init api web
  unset MIGRATION_TARGET
fi

actual_migration="$(compose exec -T postgres psql \
  --username "$(sed -n 's/^POSTGRES_USER=//p' "$environment_file" | tail -n 1 | tr -d '\r')" \
  --dbname "$(sed -n 's/^POSTGRES_DB=//p' "$environment_file" | tail -n 1 | tr -d '\r')" \
  --tuples-only --no-align \
  --command 'SELECT "MigrationId" FROM infrastructure.__ef_migrations_history ORDER BY "MigrationId" DESC LIMIT 1;' \
  | tr -d '\r')"
if [ -z "$actual_migration" ] || ! schema_is_compatible "$actual_migration" "$target_compatibility"; then
  echo "Database migration $actual_migration is not compatible with the target release." >&2
  exit 1
fi
if [ -z "$preflight_schema" ] || [ "${target_migration%%_*}" -ge "${preflight_schema%%_*}" ]; then
  if [ "$actual_migration" != "$target_migration" ]; then
    echo "Forward deployment did not reach target migration $target_migration." >&2
    exit 1
  fi
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
sed -i '/^ACTIVE_SCHEMA_ID=/d' "$temporary_state"
printf 'ACTIVE_SCHEMA_ID=%s\n' "$actual_migration" >>"$temporary_state"
chmod 0600 "$temporary_state"
mv "$temporary_state" "$state_directory/current-release.env"
release_git_sha="$(sed -n 's/^RELEASE_GIT_SHA=//p' "$release_manifest" | tail -n 1 | tr -d '\r')"
printf 'Release %s is healthy, digest-verified and recorded.\n' "$release_git_sha"
