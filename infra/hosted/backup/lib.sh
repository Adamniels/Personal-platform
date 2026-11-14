# What the backup job's commands share: its settings and its clock. Sourced by main.sh, never run.
# See "Backups" in infra/hosted/README.md.

# Where the dumps and the status are, the volumes `backups` and `backup_status`.
BACKUP_DIR=/backups
STATUS_FILE=/status/backup.json
# The hosted Postgres, by its compose name.
DB_HOST=postgres

# load_settings: checks every setting, applies the defaults, and stops the job naming each one that
# is missing or malformed. A message names a setting and never holds its value.
load_settings() {
  local var problems=""
  for var in BACKUP_DB_PASSWORD BACKUP_AGE_RECIPIENT BACKUP_S3_ENDPOINT BACKUP_S3_REGION \
    BACKUP_S3_BUCKET BACKUP_S3_KEY_ID BACKUP_S3_KEY; do
    [ -n "${!var:-}" ] || problems="$problems
  $var is not set"
  done
  BACKUP_S3_PREFIX="${BACKUP_S3_PREFIX:-platform}"
  BACKUP_AT="${BACKUP_AT:-03:00}"
  TZ="${TZ:-Europe/Stockholm}"
  BACKUP_KEEP_LOCAL_DAYS="${BACKUP_KEEP_LOCAL_DAYS:-7}"
  export TZ

  # Each format below is checked only when the setting is there, so a missing one is named once.
  # An age recipient is age1 and Bech32, lower case letters and digits.
  [[ -z "${BACKUP_AGE_RECIPIENT:-}" || "$BACKUP_AGE_RECIPIENT" =~ ^age1[02-9ac-hj-np-z]+$ ]] ||
    problems="$problems
  BACKUP_AGE_RECIPIENT is not an age recipient: it begins with age1, and is the public key, never the private one"
  [[ -z "${BACKUP_S3_ENDPOINT:-}" || "$BACKUP_S3_ENDPOINT" =~ ^https?://[A-Za-z0-9.-]+(:[0-9]+)?/?$ ]] ||
    problems="$problems
  BACKUP_S3_ENDPOINT is not an http or https origin, such as https://s3.eu-north-1.amazonaws.com"
  [[ -z "${BACKUP_S3_REGION:-}" || "$BACKUP_S3_REGION" =~ ^[a-z0-9-]+$ ]] ||
    problems="$problems
  BACKUP_S3_REGION is not a region name, lower case letters, digits and hyphens"
  [[ -z "${BACKUP_S3_BUCKET:-}" || "$BACKUP_S3_BUCKET" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] ||
    problems="$problems
  BACKUP_S3_BUCKET is not a bucket name"
  # Both go into curl's config file in double quotes, so neither may hold a quote, a backslash or
  # white space; access keys are letters, digits and a few of / + = . _ -.
  [[ -z "${BACKUP_S3_KEY_ID:-}" || "$BACKUP_S3_KEY_ID" =~ ^[A-Za-z0-9/+=._-]+$ ]] ||
    problems="$problems
  BACKUP_S3_KEY_ID holds a character an access key does not"
  [[ -z "${BACKUP_S3_KEY:-}" || "$BACKUP_S3_KEY" =~ ^[A-Za-z0-9/+=._-]+$ ]] ||
    problems="$problems
  BACKUP_S3_KEY holds a character an access key does not"
  [[ "$BACKUP_S3_PREFIX" =~ ^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$ ]] ||
    problems="$problems
  BACKUP_S3_PREFIX is not a path of letters, digits and . _ -, without a leading or trailing /"
  [[ "$BACKUP_AT" =~ ^([01][0-9]|2[0-3]):[0-5][0-9]$ ]] ||
    problems="$problems
  BACKUP_AT is not a time of day as HH:MM, 00:00 to 23:59"
  [[ "$TZ" =~ ^[A-Za-z0-9_+-]+(/[A-Za-z0-9_+-]+)*$ && -f "/usr/share/zoneinfo/$TZ" ]] ||
    problems="$problems
  TZ is not a time zone, such as Europe/Stockholm"
  [[ "$BACKUP_KEEP_LOCAL_DAYS" =~ ^[1-9][0-9]{0,3}$ ]] ||
    problems="$problems
  BACKUP_KEEP_LOCAL_DAYS is not a whole number of days, 1 or more"

  if [ -n "$problems" ]; then
    echo "backup: stopped, the settings are not right, see infra/hosted/.env.example:$problems" >&2
    exit 1
  fi
}

# offset_seconds <epoch> <zone>: the zone's offset from UTC at that instant, in seconds.
offset_seconds() {
  local z sign hours minutes
  z="$(TZ="$2" date -d "@$1" +%z)"
  sign="${z:0:1}"
  hours="${z:1:2}"
  minutes="${z:3:2}"
  local seconds=$((10#$hours * 3600 + 10#$minutes * 60))
  [ "$sign" = - ] && seconds=$((-seconds))
  echo "$seconds"
}

# local_time_epoch <YYYY-MM-DD> <HH:MM> <zone>: the instant that day's HH:MM happens in the zone.
# A time a change to summer time skips, such as 02:30 on the day clocks go from 02:00 to 03:00,
# is taken as the same distance past the gap, 03:30; a time an autumn change repeats is taken at
# its second occurrence. Either way the time happens once that day.
local_time_epoch() {
  local naive guess
  # The day's HH:MM read as UTC, then moved by the zone's offset, twice, so an offset that differs
  # either side of a change settles.
  naive="$(date -u -d "$1 $2" +%s)"
  guess=$((naive - $(offset_seconds "$naive" "$3")))
  echo $((naive - $(offset_seconds "$guess" "$3")))
}

# seconds_until_next <now, epoch> <HH:MM> <zone>: how long to sleep until the next HH:MM in the
# zone strictly after now. The time is an argument, so a test gives it any clock.
seconds_until_next() {
  local now="$1" at="$2" zone="$3" today day next i
  today="$(TZ="$zone" date -d "@$now" +%F)"
  for i in 0 1 2; do
    day="$(date -u -d "$today + $i day" +%F)"
    next="$(local_time_epoch "$day" "$at" "$zone")"
    if [ "$next" -gt "$now" ]; then
      echo $((next - now))
      return 0
    fi
  done
  return 1
}
