#!/bin/sh
set -eu

script_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
loader="$script_directory/load-operations-config.sh"
temporary_directory="$(mktemp -d)"
cleanup() { rm -rf "$temporary_directory"; }
trap cleanup EXIT HUP INT TERM
chmod 0700 "$temporary_directory"

write_valid() {
  cat >"$1" <<'EOF'
LOCAL_RETENTION_DAYS=14
OFFSITE_RETENTION_DAYS=90
BACKUP_STALE_AFTER_HOURS=36
OFFSITE_REMOTE=immutable:comic-management
ALERT_WEBHOOK_URL=https://alerts.example.invalid/d01
EOF
  chmod 0600 "$1"
}

assert_rejected() {
  candidate="$1"
  if sh -c '. "$1"; load_operations_config "$2"' sh "$loader" "$candidate" >/dev/null 2>&1; then
    echo "Unsafe operations configuration was accepted: $candidate" >&2
    exit 1
  fi
}

valid="$temporary_directory/valid.env"
write_valid "$valid"
sh -c '. "$1"; load_operations_config "$2"' sh "$loader" "$valid"

wrong_mode="$temporary_directory/wrong-mode.env"
write_valid "$wrong_mode"
chmod 0644 "$wrong_mode"
assert_rejected "$wrong_mode"

symlink="$temporary_directory/symlink.env"
ln -s "$valid" "$symlink"
assert_rejected "$symlink"

payload="$temporary_directory/payload.env"
write_valid "$payload"
printf 'ALERT_WEBHOOK_URL=$(id)\n' >>"$payload"
assert_rejected "$payload"

unknown="$temporary_directory/unknown.env"
write_valid "$unknown"
printf 'UNREVIEWED_SETTING=value\n' >>"$unknown"
assert_rejected "$unknown"

if [ "$(id -u)" -eq 0 ]; then
  wrong_owner="$temporary_directory/wrong-owner.env"
  write_valid "$wrong_owner"
  chown 65534 "$wrong_owner"
  assert_rejected "$wrong_owner"
fi

echo "Strict operations configuration parsing passed."
