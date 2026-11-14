# One backup: every database dumped and encrypted into the volume, each file uploaded, the status
# written, and old local copies removed. Sourced by main.sh after lib.sh and load_settings, never
# run. See "Backups" in infra/hosted/README.md.

# write_status <attempt time> <ok|dump|encrypt|upload>: the status file, replaced in one step by
# writing a temporary file beside it and renaming it over the old one, so a reader never sees half
# of it. last_success_at survives a failed attempt. last_error is the step's fixed name, never a
# command's output.
write_status() {
  local at="$1" outcome="$2" ok success error temp earlier
  if [ "$outcome" = ok ]; then
    ok=true
    success="\"$at\""
    error=null
  else
    ok=false
    error="\"$outcome\""
    # The file is only ever written here, one key to a line, so the earlier value is read back
    # by its line, and only when it is a time in the format written.
    earlier="$(sed -nE 's/^  "last_success_at": "([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z)",$/\1/p' \
      "$STATUS_FILE" 2> /dev/null)"
    success=null
    [ -n "$earlier" ] && success="\"$earlier\""
  fi
  temp="$(mktemp "$STATUS_FILE.XXXXXX")" || {
    echo "backup: cannot write the status file" >&2
    return 1
  }
  printf '{\n  "last_attempt_at": "%s",\n  "last_attempt_ok": %s,\n  "last_success_at": %s,\n  "last_error": %s\n}\n' \
    "$at" "$ok" "$success" "$error" > "$temp" &&
    chmod 0644 "$temp" &&
    mv -f "$temp" "$STATUS_FILE" || {
    rm -f "$temp"
    echo "backup: cannot write the status file" >&2
    return 1
  }
}

# attempt_failed <attempt time> <step>: records the failed attempt and ends the run, exiting 1.
# Files already made are kept.
attempt_failed() {
  write_status "$1" "$2"
  echo "backup: failed at $2; the files of this run are kept in $BACKUP_DIR" >&2
  exit 1
}

# upload <file>: one signed PUT to the bucket, with the Content-MD5 an Object Lock bucket requires.
# The key id and the key reach curl as a config file on a file descriptor, written by printf, a
# bash builtin, so neither is ever in an argument list.
upload() {
  local file="$1" md5 url
  md5="$(openssl dgst -md5 -binary < "$file" | base64)" || return 1
  url="${BACKUP_S3_ENDPOINT%/}/$BACKUP_S3_BUCKET/$BACKUP_S3_PREFIX/${file##*/}"
  curl --fail --silent --show-error --retry 3 \
    --config <(printf 'user = "%s:%s"\n' "$BACKUP_S3_KEY_ID" "$BACKUP_S3_KEY") \
    --aws-sigv4 "aws:amz:$BACKUP_S3_REGION:s3" \
    --header "Content-MD5: $md5" \
    --upload-file "$file" \
    --output /dev/null \
    "$url"
}

# remove_old_local: after a successful run, the local dumps dated, by the time in their names,
# more than BACKUP_KEEP_LOCAL_DAYS days ago, and any partial file a killed run left. The bucket
# keeps its own copies by its own rule, and nothing here deletes from it.
remove_old_local() {
  local cutoff file name stamp when
  cutoff=$(($(date -u +%s) - BACKUP_KEEP_LOCAL_DAYS * 86400))
  for file in "$BACKUP_DIR"/*.dump.age; do
    [ -e "$file" ] || continue
    name="${file##*/}"
    [[ "$name" =~ ^([0-9]{4})([0-9]{2})([0-9]{2})T([0-9]{2})([0-9]{2})([0-9]{2})Z-.+\.dump\.age$ ]] || continue
    stamp="${BASH_REMATCH[1]}-${BASH_REMATCH[2]}-${BASH_REMATCH[3]} ${BASH_REMATCH[4]}:${BASH_REMATCH[5]}:${BASH_REMATCH[6]}"
    when="$(date -u -d "$stamp" +%s 2> /dev/null)" || continue
    if [ "$when" -lt "$cutoff" ]; then
      rm -f -- "$file" && echo "backup: removed $name, older than $BACKUP_KEEP_LOCAL_DAYS days"
    fi
  done
  # Under the lock no other run is writing, so every partial file is one a killed run left.
  rm -f -- "$BACKUP_DIR"/*.partial
}

# run_backup: one attempt, which succeeds only when every database was dumped and every file
# uploaded. Exits non zero on any failure, with the status saying which step.
run_backup() {
  local started at stamp databases database file codes
  local -a made=()

  # One run at a time, the scheduled one and one started by hand, so two never write the same
  # file, the status or the retention at once. The second waits for the first.
  exec 9> "$BACKUP_DIR/.lock"
  flock 9

  started="$(date -u +%s)"
  at="$(date -u -d "@$started" +%Y-%m-%dT%H:%M:%SZ)"
  stamp="$(date -u -d "@$started" +%Y%m%dT%H%M%SZ)"
  umask 077

  # Every database but postgres and the templates, asked of Postgres, so none is forgotten.
  export PGHOST="$DB_HOST" PGUSER=platform_backup PGPASSWORD="$BACKUP_DB_PASSWORD" PGCONNECT_TIMEOUT=10
  if ! databases="$(psql -w -X -A -t -d postgres -v ON_ERROR_STOP=1 \
    -c "SELECT datname FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres' ORDER BY datname")"; then
    echo "backup: cannot list the databases as platform_backup" >&2
    attempt_failed "$at" dump
  fi
  if [ -z "$databases" ]; then
    echo "backup: Postgres lists no database to back up" >&2
    attempt_failed "$at" dump
  fi

  for database in $databases; do
    # A name is a file name and pg_dump's --dbname, so it must be a plain one; a database that is
    # not fails the run rather than being left out of it.
    if ! [[ "$database" =~ ^[A-Za-z0-9_]+$ ]]; then
      echo "backup: the database name '$database' is not letters, digits and _" >&2
      attempt_failed "$at" dump
    fi
    file="$BACKUP_DIR/$stamp-$database.dump.age"
    # Straight from pg_dump into age, so nothing unencrypted touches a disk.
    pg_dump -w --format=custom --dbname="$database" | age -r "$BACKUP_AGE_RECIPIENT" > "$file.partial"
    codes=("${PIPESTATUS[@]}")
    if [ "${codes[0]}" -ne 0 ]; then
      rm -f -- "$file.partial"
      echo "backup: pg_dump of $database failed" >&2
      attempt_failed "$at" dump
    fi
    if [ "${codes[1]}" -ne 0 ]; then
      rm -f -- "$file.partial"
      echo "backup: encrypting $database failed" >&2
      attempt_failed "$at" encrypt
    fi
    mv -f -- "$file.partial" "$file"
    made+=("$file")
    echo "backup: dumped $database"
  done

  for file in "${made[@]}"; do
    if ! upload "$file"; then
      echo "backup: uploading ${file##*/} failed" >&2
      attempt_failed "$at" upload
    fi
    echo "backup: uploaded ${file##*/}"
  done

  write_status "$at" ok || exit 1
  echo "backup: $at, ${#made[@]} databases dumped and uploaded"
  remove_old_local
}
