#!/bin/sh

# This file is trusted application code. It parses operator-owned data without
# evaluating it as shell input. Call load_operations_config <path>.
load_operations_config() {
  operations_file="$1"

  if [ -L "$operations_file" ] || [ ! -f "$operations_file" ]; then
    echo "Operations configuration must be a regular, non-symlink file." >&2
    return 66
  fi

  operations_mode="$(stat -c '%a' "$operations_file" 2>/dev/null || true)"
  case "$operations_mode" in
    400|600) ;;
    *) echo "Operations configuration must have mode 0400 or 0600." >&2; return 64 ;;
  esac

  if [ "$(id -u)" -eq 0 ]; then
    operations_owner="$(stat -c '%u' "$operations_file" 2>/dev/null || true)"
    if [ "$operations_owner" != "0" ]; then
      echo "Root services require root-owned operations configuration." >&2
      return 64
    fi
  fi

  seen_keys=''
  while IFS= read -r raw_line || [ -n "$raw_line" ]; do
    line="$(printf '%s' "$raw_line" | tr -d '\r')"
    case "$line" in
      ''|'#'*) continue ;;
      *=*) ;;
      *) echo "Operations configuration must use strict KEY=VALUE lines." >&2; return 64 ;;
    esac

    key="${line%%=*}"
    value="${line#*=}"
    case "$key" in
      LOCAL_RETENTION_DAYS|OFFSITE_RETENTION_DAYS|BACKUP_STALE_AFTER_HOURS|OFFSITE_REMOTE|ALERT_WEBHOOK_URL) ;;
      *) echo "Unknown operations configuration key: $key" >&2; return 64 ;;
    esac
    case " $seen_keys " in
      *" $key "*) echo "Duplicate operations configuration key: $key" >&2; return 64 ;;
    esac
    seen_keys="$seen_keys $key"

    case "$value" in
      *'`'*|*'$('*|*'$ {'*|*';'*|*'&&'*|*'||'*)
        echo "Operations configuration contains forbidden shell syntax: $key" >&2
        return 64
        ;;
    esac

    case "$key" in
      LOCAL_RETENTION_DAYS) LOCAL_RETENTION_DAYS="$value" ;;
      OFFSITE_RETENTION_DAYS) OFFSITE_RETENTION_DAYS="$value" ;;
      BACKUP_STALE_AFTER_HOURS) BACKUP_STALE_AFTER_HOURS="$value" ;;
      OFFSITE_REMOTE) OFFSITE_REMOTE="$value" ;;
      ALERT_WEBHOOK_URL) ALERT_WEBHOOK_URL="$value" ;;
    esac
  done <"$operations_file"
}
