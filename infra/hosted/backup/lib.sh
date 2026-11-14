# What the backup job's commands share: its settings. Sourced by main.sh, never run.
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
