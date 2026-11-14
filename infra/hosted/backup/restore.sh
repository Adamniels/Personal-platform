# restore <database> <file> [--as <name>] [--replace]: decrypts one dump with the age identity
# given on standard input and restores it, as the superuser, so owners and grants come back as they
# were. Sourced by main.sh after lib.sh, never run. See "Restoring" in infra/hosted/README.md.
#
# The target, <database> or the --as name, must have no table in public, or it is refused unless
# --replace is given, which drops it and creates it again. A target that does not exist is created.
# Either way it is created owned by the owner of the database it replaces, or of <database> when
# there is none, with the same grants on it, so the app role can connect as before.

restore_usage() {
  echo "usage: restore <database> <file> [--as <name>] [--replace] < private-key" >&2
  exit 2
}

restore_error() {
  echo "restore: $*" >&2
  exit 1
}

# super_sql <database>: statements on stdin as the superuser, unaligned and without headers.
super_sql() {
  psql -w -X -A -t -q -v ON_ERROR_STOP=1 -d "$1"
}

restore() {
  local database="" file="" target="" replace=false identity model exists tables owner grants
  local -a codes

  while [ $# -gt 0 ]; do
    case "$1" in
      --as)
        [ $# -ge 2 ] || restore_usage
        target="$2"
        shift 2
        ;;
      --replace)
        replace=true
        shift
        ;;
      -*) restore_usage ;;
      *)
        if [ -z "$database" ]; then
          database="$1"
        elif [ -z "$file" ]; then
          file="$1"
        else
          restore_usage
        fi
        shift
        ;;
    esac
  done
  [ -n "$database" ] && [ -n "$file" ] || restore_usage
  target="${target:-$database}"
  # Both go into SQL unquoted and into --dbname, so each must be a plain identifier.
  for name in "$database" "$target"; do
    [[ "$name" =~ ^[a-z_][a-z0-9_]*$ ]] || restore_error "'$name' is not a lowercase database name"
  done
  [ "$target" != postgres ] || restore_error "postgres is never restored into"
  # A bare name is a file in the volume.
  case "$file" in */*) ;; *) file="$BACKUP_DIR/$file" ;; esac
  [ -r "$file" ] || restore_error "cannot read $file"

  [ -n "${POSTGRES_PASSWORD:-}" ] ||
    restore_error "POSTGRES_PASSWORD is not set: give it to this run alone, with -e POSTGRES_PASSWORD"
  export PGHOST="$DB_HOST" PGUSER=postgres PGPASSWORD="$POSTGRES_PASSWORD" PGCONNECT_TIMEOUT=10

  # The identity, read once from standard input and held in memory, is handed to age on a pipe
  # each time, so it is never written to a file.
  [ ! -t 0 ] || restore_error "give the age identity on standard input: < private-key"
  identity="$(cat)"
  [[ "$identity" == *AGE-SECRET-KEY-1* ]] || restore_error "standard input holds no age identity"

  # The file is checked before anything changes: it decrypts with this identity, all of it, since
  # age authenticates every chunk, and what it holds is a pg_dump archive.
  age -d -i <(printf '%s\n' "$identity") "$file" > /dev/null ||
    restore_error "cannot decrypt $file with that identity"
  age -d -i <(printf '%s\n' "$identity") "$file" | pg_restore --list > /dev/null
  codes=("${PIPESTATUS[@]}")
  [ "${codes[1]}" -eq 0 ] || restore_error "$file does not hold a pg_dump archive"

  exists="$(echo "SELECT 1 FROM pg_database WHERE datname = '$target'" | super_sql postgres)" ||
    restore_error "cannot reach Postgres as the superuser"
  if [ "$exists" = 1 ]; then
    tables="$(echo "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'" | super_sql "$target")" ||
      restore_error "cannot read $target"
    if [ "$tables" != 0 ] && [ "$replace" != true ]; then
      restore_error "$target has $tables tables in public; nothing was changed. Restore under another name with --as, or give --replace to drop it and restore over it"
    fi
    model="$target"
  else
    [ "$(echo "SELECT 1 FROM pg_database WHERE datname = '$database'" | super_sql postgres)" = 1 ] ||
      restore_error "no database $target to restore into, and no $database to take its owner and grants from; a fresh volume's init scripts create each service's database"
    model="$database"
  fi

  # The target's owner and its grants, from the database it replaces or the one it is a copy of,
  # read before anything is dropped.
  if [ "$exists" != 1 ] || [ "$replace" = true ]; then
    owner="$(echo "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = '$model'" | super_sql postgres)" ||
      restore_error "cannot read the owner of $model"
    grants="$(super_sql postgres << SQL
SELECT 'REVOKE ALL ON DATABASE $target FROM PUBLIC;'
FROM pg_database WHERE datname = '$model' AND datacl IS NOT NULL;
SELECT format('GRANT %s ON DATABASE %I TO %s%s;', a.privilege_type, '$target',
  CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(a.grantee)) END,
  CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END)
FROM pg_database d CROSS JOIN aclexplode(d.datacl) a
WHERE d.datname = '$model'
ORDER BY a.grantee, a.privilege_type;
SQL
)" || restore_error "cannot read the grants on $model"
    # One statement per line, not one transaction, since DROP and CREATE DATABASE cannot run in
    # one; as scripts/agent/reset-db.sh does.
    {
      [ "$exists" = 1 ] && echo "DROP DATABASE $target WITH (FORCE);"
      echo "CREATE DATABASE $target OWNER $(printf '%s' "$owner" | sed 's/"/""/g; s/.*/"&"/');"
      printf '%s\n' "$grants"
    } | super_sql postgres || restore_error "could not create $target"
    echo "restore: created $target, owned by $owner, with the grants of $model"
  fi

  # One transaction: all of the dump or none of it.
  age -d -i <(printf '%s\n' "$identity") "$file" |
    pg_restore -w --exit-on-error --single-transaction --dbname="$target"
  codes=("${PIPESTATUS[@]}")
  if [ "${codes[0]}" -ne 0 ] || [ "${codes[1]}" -ne 0 ]; then
    restore_error "pg_restore failed, and rolled back; $target is left as it was before it, empty when it was created"
  fi
  echo "restore: ${file##*/} restored into $target"
}
