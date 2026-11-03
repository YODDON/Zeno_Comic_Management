#!/bin/sh
set -eu

repository_root="$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)"
temporary_directory="$(mktemp -d)"
cleanup() { rm -rf "$temporary_directory"; }
trap cleanup EXIT HUP INT TERM

operations_environment="$temporary_directory/operations.env"
missing_release="$temporary_directory/missing-current-release.env"
cat >"$operations_environment" <<'EOF'
LOCAL_RETENTION_DAYS=7
OFFSITE_RETENTION_DAYS=30
BACKUP_STALE_AFTER_HOURS=36
OFFSITE_REMOTE=remote:backups
ALERT_WEBHOOK_URL=
EOF
chmod 0600 "$operations_environment"

if DEPLOYMENT_ROOT="$repository_root" \
    DEPLOYMENT_ENVIRONMENT="$repository_root/.env.example" \
    OPERATIONS_ENVIRONMENT="$operations_environment" \
    DEPLOYMENT_RELEASE_MANIFEST="$missing_release" \
    sh "$repository_root/deployment/scripts/run-backup-job.sh" >/dev/null 2>&1; then
  echo "Scheduled backup accepted a missing authoritative release state." >&2
  exit 1
fi

if DEPLOYMENT_ROOT="$repository_root" \
    DEPLOYMENT_ENVIRONMENT="$repository_root/.env.example" \
    DEPLOYMENT_RELEASE_MANIFEST="$missing_release" \
    sh "$repository_root/deployment/scripts/run-restore-drill-job.sh" >/dev/null 2>&1; then
  echo "Restore drill accepted a missing authoritative release state." >&2
  exit 1
fi

echo "Backup and restore release-state guards passed."
