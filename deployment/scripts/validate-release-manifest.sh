#!/bin/sh
set -eu

manifest_file="${1:-}"
if [ -z "$manifest_file" ] || [ ! -f "$manifest_file" ]; then
  echo "Release manifest not found: ${manifest_file:-<missing>}" >&2
  exit 66
fi

manifest_value() {
  key="$1"
  count="$(grep -c "^${key}=" "$manifest_file" || true)"
  if [ "$count" -ne 1 ]; then
    echo "Release manifest must contain exactly one $key." >&2
    exit 64
  fi
  value="$(sed -n "s/^${key}=//p" "$manifest_file" | tail -n 1 | tr -d '\r')"
  if [ -z "$value" ]; then
    echo "Release manifest is missing $key." >&2
    exit 64
  fi
  printf '%s' "$value"
}

release_git_sha="$(manifest_value RELEASE_GIT_SHA)"
release_created_at="$(manifest_value RELEASE_CREATED_AT)"
current_migration_id="$(manifest_value CURRENT_MIGRATION_ID)"
compatible_schema_ids="$(manifest_value COMPATIBLE_SCHEMA_IDS)"
if ! printf '%s' "$release_git_sha" | grep -Eq '^[0-9a-f]{40}([0-9a-f]{24})?$'; then
  echo "RELEASE_GIT_SHA must be a full lowercase Git object ID." >&2
  exit 64
fi
if ! printf '%s' "$release_created_at" | grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'; then
  echo "RELEASE_CREATED_AT must be an RFC 3339 UTC timestamp without fractions." >&2
  exit 64
fi
if ! printf '%s' "$current_migration_id" | grep -Eq '^[0-9]{14}_[A-Za-z0-9_]{1,185}$'; then
  echo "CURRENT_MIGRATION_ID has an invalid format." >&2
  exit 64
fi
if ! printf '%s' "$compatible_schema_ids" | grep -Eq '^[0-9]{14}_[A-Za-z0-9_]{1,185}(,[0-9]{14}_[A-Za-z0-9_]{1,185})*$'; then
  echo "COMPATIBLE_SCHEMA_IDS must be a comma-separated migration allowlist." >&2
  exit 64
fi
case ",$compatible_schema_ids," in
  *",$current_migration_id,"*) ;;
  *) echo "COMPATIBLE_SCHEMA_IDS must include CURRENT_MIGRATION_ID." >&2; exit 64 ;;
esac

active_schema_count="$(grep -c '^ACTIVE_SCHEMA_ID=' "$manifest_file" || true)"
if [ "$active_schema_count" -gt 1 ]; then
  echo "Release state may contain at most one ACTIVE_SCHEMA_ID." >&2
  exit 64
fi
if [ "$active_schema_count" -eq 1 ]; then
  active_schema_id="$(sed -n 's/^ACTIVE_SCHEMA_ID=//p' "$manifest_file" | tr -d '\r')"
  case ",$compatible_schema_ids," in
    *",$active_schema_id,"*) ;;
    *) echo "ACTIVE_SCHEMA_ID is not compatible with this release." >&2; exit 64 ;;
  esac
fi

for component in API WEB MIGRATOR OPS; do
  reference="$(manifest_value "${component}_IMAGE_REFERENCE")"
  digest="$(manifest_value "${component}_IMAGE_DIGEST")"
  if ! printf '%s' "$digest" | grep -Eq '^sha256:[0-9a-f]{64}$'; then
    echo "${component}_IMAGE_DIGEST must be a sha256 OCI digest." >&2
    exit 64
  fi
  case "$reference" in
    *@"$digest") ;;
    *) echo "${component}_IMAGE_REFERENCE must be pinned to ${component}_IMAGE_DIGEST." >&2; exit 64 ;;
  esac
  if ! printf '%s' "$reference" | grep -Eq '^[a-z0-9._:/-]+@sha256:[0-9a-f]{64}$'; then
    echo "${component}_IMAGE_REFERENCE is not a canonical lowercase digest reference." >&2
    exit 64
  fi
  if printf '%s' "$reference" | grep -Eqi '(^|[:/])latest(@|$)'; then
    echo "latest is forbidden in release image references." >&2
    exit 64
  fi
done

echo "Release manifest pins API, web, migrator and operations images by digest."
