#!/usr/bin/env bash
# The backup image's entry point. Its commands:
#   schedule  the default: sleeps until the next BACKUP_AT in TZ, runs a backup, and repeats
#   now       one backup, now; exits non zero if it failed
#   restore <database> <file> [--as <name>] [--replace] < private-key
# See "Backups" in infra/hosted/README.md.
set -uo pipefail

lib=/usr/local/lib/backup
. "$lib/lib.sh"

case "${1:-schedule}" in
  schedule)
    . "$lib/backup.sh"
    . "$lib/schedule.sh"
    load_settings
    schedule
    ;;
  now)
    . "$lib/backup.sh"
    load_settings
    run_backup
    ;;
  restore)
    shift
    . "$lib/restore.sh"
    restore "$@"
    ;;
  *)
    echo "usage: schedule | now | restore <database> <file> [--as <name>] [--replace] < private-key" >&2
    exit 2
    ;;
esac
