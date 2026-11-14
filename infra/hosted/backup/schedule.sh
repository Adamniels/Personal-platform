# The backup service's default command: sleep until the next BACKUP_AT in TZ, run a backup, and
# repeat. Sourced by main.sh after lib.sh, backup.sh and load_settings, never run. See "Backups"
# in infra/hosted/README.md.

schedule() {
  local seconds sleeper=""
  # docker stop sends SIGTERM; the sleep is waited for in the background, so the trap runs at
  # once rather than when the sleep ends.
  trap '[ -n "$sleeper" ] && kill "$sleeper" 2> /dev/null; exit 0' TERM INT
  while true; do
    if ! seconds="$(seconds_until_next "$(date +%s)" "$BACKUP_AT" "$TZ")"; then
      echo "backup: cannot work out the next $BACKUP_AT in $TZ" >&2
      exit 1
    fi
    echo "backup: next backup at $(date -d "@$(($(date +%s) + seconds))" '+%Y-%m-%d %H:%M %Z'), in $seconds seconds"
    sleep "$seconds" &
    sleeper=$!
    wait "$sleeper"
    sleeper=""
    # In a subshell, since a failed attempt exits; the failure is in the status file and the log,
    # and the schedule goes on to the next night.
    (run_backup) || true
  done
}
