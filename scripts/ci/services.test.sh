#!/usr/bin/env bash
# Checks infra/services.json, the service manifest, on its own and against the three files that
# describe the same routes and databases: infra/features.toml, infra/Caddyfile and
# infra/.env.example. Nothing else checks that they agree. See "The service manifest" in
# infra/README.md.
# The same checks run on broken copies of the files, so they cannot pass by matching nothing.
# Run: bash scripts/ci/services.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd -P)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
pass=0
fail=0

ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; [ -n "${2:-}" ] && echo "     $2"; fail=$((fail + 1)); }

# problems <root>: prints one line per problem found in <root>'s manifest, nothing when it is
# sound. <root> holds infra/services.json, infra/features.toml, infra/Caddyfile,
# infra/.env.example, the services' folders, scripts/ci/changed-areas.sh and
# scripts/agent/services.sh.
problems() {
  local root="$1" manifest="$1/infra/services.json"

  if ! jq -e . "$manifest" > /dev/null 2>&1; then
    echo "manifest does not parse"
    return
  fi

  # Shape and types, one line per problem, from jq.
  jq -r '
    def str: type == "string" and length > 0;
    def ident: str and test("^[a-z_][a-z0-9_]*$");
    def var: str and test("^[A-Z_][A-Z0-9_]*$");
    if (type != "object") or (keys != ["services"]) or (.services | type != "array") or (.services | length == 0)
    then "the manifest is not one object {\"services\": [ ... ]} with at least one entry"
    else
      .services | to_entries[] | .key as $i | .value as $s |
      ("entry \($i)" + (if ($s | type == "object") and ($s.name | str) then " (\($s.name))" else "" end)) as $at |
      if ($s | type) != "object" then "\($at): not an object"
      else
        (($s | keys) - ["name", "dir", "port", "health", "language", "area", "migrate", "start", "database"]
          | .[] | "\($at): unknown field \(.)"),
        (["name", "dir", "port", "language", "area", "start"] - ($s | keys) | .[] | "\($at): missing \(.)"),
        (if ($s | has("name")) and (($s.name | str) and ($s.name | test("^[a-z0-9-]+$")) | not)
          then "\($at): name is not lowercase letters, digits and hyphens" else empty end),
        (if ($s | has("dir")) and (($s.dir | str) and ($s.dir | test("^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$")) | not)
          then "\($at): dir is not a relative folder" else empty end),
        (if ($s | has("port")) and (($s.port | type == "number") and ($s.port | floor == .) and $s.port >= 1 and $s.port <= 65535 | not)
          then "\($at): port is not an integer from 1 to 65535" else empty end),
        (if ($s | has("health")) and (($s.health | str) and ($s.health | startswith("/")) | not)
          then "\($at): health is not a path" else empty end),
        (if ($s | has("language")) and (["rust", "python", "dotnet"] | index([$s.language]) | not)
          then "\($at): language is not rust, python or dotnet" else empty end),
        (if ($s | has("area")) and ($s.area | str | not) then "\($at): area is not a string" else empty end),
        (if ($s | has("migrate")) and ($s.migrate | str | not) then "\($at): migrate is not a command" else empty end),
        (if ($s | has("start")) and ($s.start | str | not) then "\($at): start is not a command" else empty end),
        (if ($s | has("database")) then
          $s.database as $d |
          if ($d | type) != "object" then "\($at): database is not an object"
          else
            (($d | keys) - ["name", "owner", "app", "owner_password", "app_password", "owner_url", "app_url"]
              | .[] | "\($at): database has unknown field \(.)"),
            (["name", "owner", "app", "owner_password", "app_password", "owner_url"] - ($d | keys)
              | .[] | "\($at): database misses \(.)"),
            (["name", "owner", "app"][] as $k | select($d | has($k)) | select($d[$k] | ident | not)
              | "\($at): database \($k) is not a lowercase identifier"),
            (["owner_password", "app_password", "owner_url", "app_url"][] as $k | select($d | has($k))
              | select($d[$k] | var | not) | "\($at): database \($k) is not a variable name"),
            (if ($s | has("migrate") | not) then "\($at): has a database but no migrate" else empty end)
          end
        else empty end)
      end
    end
  ' "$manifest"

  # Unique names, folders, ports and databases.
  local field dupes
  for field in .name .dir .port .database.name; do
    dupes="$(jq -r ".services[] | $field // empty" "$manifest" 2>/dev/null | sort | uniq -d)"
    [ -n "$dupes" ] && echo "${field#.} not unique: $(echo "$dupes" | tr '\n' ' ')"
  done

  # Each folder's change sets its area, through changed-areas.sh itself, so an area that script
  # cannot set fails here and not in CI.
  local area out
  while read -r dir area; do
    [ -z "$dir" ] && continue
    if ! out="$(printf '%s/x' "$dir" | bash "$root/scripts/ci/changed-areas.sh" 2>&1)" ||
      ! printf '%s\n' "$out" | grep -qx "$area=true"; then
      echo "$dir: changed-areas.sh does not set area $area for it"
    fi
  done < <(jq -r '.services[] | "\(.dir) \(.area)"' "$manifest" 2>/dev/null)

  # Every folder exists.
  local dir
  while IFS= read -r dir; do
    [ -n "$dir" ] && [ ! -d "$root/$dir" ] && echo "dir $dir does not exist"
  done < <(jq -r '.services[] | .dir // empty | strings' "$manifest" 2>/dev/null)

  # Every password variable is listed in .env.example.
  local var
  while IFS= read -r var; do
    [ -z "$var" ] && continue
    grep -q "^$var=" "$root/infra/.env.example" || echo "$var is not in infra/.env.example"
  done < <(jq -r '.services[] | .database? // empty | objects | .owner_password, .app_password | strings' "$manifest" 2>/dev/null)

  # The Caddyfile's routes: one "<path> <port>" line per path a matcher names, for each
  # `handle @matcher` holding a reverse_proxy, and "* <port>" for a bare `handle`.
  local routes
  routes="$(awk '
    $1 ~ /^@/ && $2 == "path" { for (i = 3; i <= NF; i++) paths[$1] = paths[$1] " " $i; next }
    $1 == "handle" { current = ($2 == "{") ? "*" : $2; next }
    $1 == "reverse_proxy" && current != "" {
      port = $2; sub(/.*:/, "", port)
      if (current == "*") print "* " port
      else { n = split(paths[current], p, " "); for (i = 1; i <= n; i++) print p[i] " " port }
    }
    $1 == "}" { current = "" }
  ' "$root/infra/Caddyfile")"

  # Every port the Caddyfile proxies to is a service's.
  local ports port
  ports=" $(jq -r '.services[].port' "$manifest" 2>/dev/null | tr '\n' ' ') "
  while IFS=' ' read -r _ port; do
    [ -z "$port" ] && continue
    case "$ports" in *" $port "*) ;; *) echo "the Caddyfile proxies to port $port, which no service has" ;; esac
  done <<< "$routes"

  # Every feature: a service on its upstream's port, and the Caddyfile's route for its prefix on
  # the same port.
  local prefix upstream up_port caddy_port
  while IFS=' ' read -r prefix upstream; do
    [ -z "$prefix" ] && continue
    up_port="$(printf '%s' "$upstream" | sed -nE 's#^https?://[^/:]+:([0-9]+)$#\1#p')"
    if [ -z "$up_port" ]; then
      echo "feature $prefix: upstream $upstream has no port"
      continue
    fi
    case "$ports" in *" $up_port "*) ;; *) echo "feature $prefix: no service has port $up_port" ;; esac
    caddy_port="$(printf '%s\n' "$routes" | awk -v p="$prefix" '$1 == p { print $2; exit }')"
    if [ -z "$caddy_port" ]; then
      echo "feature $prefix: the Caddyfile has no reverse_proxy for it"
    elif [ "$caddy_port" != "$up_port" ]; then
      echo "feature $prefix: the Caddyfile proxies to $caddy_port, the upstream is $up_port"
    fi
  done < <(awk -F'"' '
    /^\[\[feature\]\]/ { if (prefix != "") print prefix " " upstream; prefix = ""; upstream = ""; next }
    /^[ \t]*prefix[ \t]*=/ { prefix = $2 }
    /^[ \t]*upstream[ \t]*=/ { upstream = $2 }
    END { if (prefix != "") print prefix " " upstream }
  ' "$root/infra/features.toml")
}

# The repository's own files.
got="$(problems "$repo")"
if [ -z "$got" ] && [ -f "$repo/infra/services.json" ]; then
  ok "the manifest is sound and agrees with features.toml, the Caddyfile and .env.example"
else
  bad "the manifest is sound and agrees with features.toml, the Caddyfile and .env.example" "$(printf '%s' "${got:-no infra/services.json}" | tr '\n' ';')"
fi

# The three services the platform runs today, in start order.
names="$(jq -r '.services[].name' "$repo/infra/services.json" 2>/dev/null | tr '\n' ' ')"
if [ "$names" = "core canary brain " ]; then ok "describes the core, canary and the brain, in that order"; else bad "describes the core, canary and the brain, in that order" "got: $names"; fi

# Broken copies. expect <name> <pattern> <jq filter on the manifest> [<file in infra/> <sed script>]
# copies the repository's four files and the services' folders into a fresh root, applies the
# change, and expects a problem matching <pattern>. A filter jq cannot run is written as the
# manifest itself, for a manifest that does not parse.
expect() {
  local name="$1" pattern="$2" filter="$3" file="${4:-}" script="${5:-}" root
  root="$tmp/$name"
  mkdir -p "$root/infra" "$root/scripts/ci" "$root/scripts/agent"
  cp "$repo/scripts/ci/changed-areas.sh" "$root/scripts/ci/"
  cp "$repo/scripts/agent/services.sh" "$root/scripts/agent/"
  cp "$repo/infra/features.toml" "$repo/infra/Caddyfile" "$repo/infra/.env.example" "$root/infra/"
  jq -r '.services[].dir' "$repo/infra/services.json" | while IFS= read -r d; do mkdir -p "$root/$d"; done
  if ! jq "$filter" "$repo/infra/services.json" > "$root/infra/services.json" 2>/dev/null; then
    printf '%s' "$filter" > "$root/infra/services.json"
  fi
  if [ -n "$file" ]; then
    sed -E "$script" "$root/infra/$file" > "$root/infra/$file.new" && mv "$root/infra/$file.new" "$root/infra/$file"
  fi
  got="$(problems "$root")"
  if printf '%s\n' "$got" | grep -qE "$pattern"; then
    ok "refuses $name"
  else
    bad "refuses $name" "expected /$pattern/, got: $(printf '%s' "$got" | tr '\n' ';')"
  fi
}

expect "a manifest that does not parse" "does not parse" '{"services": ['
expect "a missing field" "missing start" 'del(.services[1].start)'
expect "a port that is a string" "port is not an integer" '.services[0].port = "8080"'
expect "a name with capitals" "name is not lowercase" '.services[0].name = "Core"'
expect "an unknown language" "language is not" '.services[0].language = "go"'
expect "an area changed-areas.sh does not print" "does not set area nowhere" '.services[0].area = "nowhere"'
expect "an area changed-areas.sh prints but never sets for a folder" "does not set area web" '.services[0].area = "web"'
expect "a misspelt field" "unknown field heath" '.services[2].heath = "/x"'
expect "a database without its owner's variable" "database misses owner_url" 'del(.services[0].database.owner_url)'
expect "a database role that is not an identifier" "database owner is not" '.services[0].database.owner = "core owner; drop"'
expect "a duplicate name" "name not unique" '.services[1].name = "core"'
expect "a duplicate port" "port not unique" '.services[1].port = 8080'
expect "a duplicate folder" "dir not unique" '.services[1].dir = "Core"'
expect "a folder that does not exist" "dir Nowhere does not exist" '.services[1].dir = "Nowhere"'
expect "a feature whose upstream port has no service" "no service has port 8081" '.services[1].port = 8091'
expect "a Caddyfile route on another port than the upstream" "proxies to 8083, the upstream is 8081" '.' \
  Caddyfile 's/host.docker.internal:8081/host.docker.internal:8083/'
expect "a feature without a Caddyfile route" "has no reverse_proxy" '.' \
  features.toml 's#^prefix = "/canary"#prefix = "/elsewhere"#'
expect "a password variable missing from .env.example" "BRAIN_APP_PASSWORD is not in" '.' \
  .env.example 's/^BRAIN_APP_PASSWORD=$//'

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
