#!/usr/bin/env bash
# Checks that the hosted stack in infra/hosted/ agrees with the files the dev stack and the
# services already describe: the service manifest, infra/services.json; the dev registry,
# infra/features.toml; and the dev Caddyfile, infra/Caddyfile. It also checks the Dockerfiles
# install only from lockfiles. It reads files only and needs no Docker; infra/hosted/check.sh runs
# it first. See infra/hosted/README.md.
# The same checks run on broken copies of the files, so they cannot pass by matching nothing.
# Run: bash infra/hosted/tests/agree.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd -P)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
pass=0
fail=0

ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; [ -n "${2:-}" ] && echo "     $2"; fail=$((fail + 1)); }

# The files every check reads, relative to a root.
FILES="infra/services.json infra/features.toml infra/Caddyfile
infra/hosted/compose.yaml infra/hosted/Caddyfile infra/hosted/features.toml infra/hosted/.env.example
Core/Dockerfile canary/Dockerfile Brain/Dockerfile review/Dockerfile devflow/Dockerfile
devflow/worker.Dockerfile infra/hosted/proxy/Dockerfile"

# block <compose file> <service>: prints the lines of one service's entry under `services:`,
# without its first line, nothing when there is no such service.
block() {
  awk -v s="$2" '
    /^[^ #]/ { in_services = ($0 ~ /^services:/); inside = 0; next }
    !in_services { next }
    /^  [^ #]/ { name = $1; sub(/:$/, "", name); inside = (name == s); next }
    inside && !/^[ \t]*#/ { print }
  ' "$1"
}

# features <features.toml>: one "name|prefix|display|icon|attention|upstream" line per [[feature]], in order,
# with the upstream last, which check_registry reads apart from the rest.
features() {
  awk -F'"' '
    function flush() { if (seen) print name "|" prefix "|" display "|" icon "|" attention "|" upstream; seen = 0; name = prefix = display = icon = attention = upstream = "" }
    /^\[\[feature\]\]/ { flush(); seen = 1; next }
    /^[ \t]*name[ \t]*=/ { name = $2 }
    /^[ \t]*prefix[ \t]*=/ { prefix = $2 }
    /^[ \t]*display[ \t]*=/ { display = $2 }
    /^[ \t]*icon[ \t]*=/ { icon = $2 }
    /^[ \t]*attention[ \t]*=/ { attention = $2 }
    /^[ \t]*upstream[ \t]*=/ { upstream = $2 }
    END { flush() }
  ' "$1"
}

# routes <Caddyfile>: one "<path> <upstream>" line per path a matcher names, for each
# `handle @matcher` holding a reverse_proxy, and "* <upstream>" for a bare `handle`.
routes() {
  awk '
    $1 ~ /^@/ && $2 == "path" { for (i = 3; i <= NF; i++) paths[$1] = paths[$1] " " $i; next }
    $1 == "handle" { current = ($2 == "{") ? "*" : $2; next }
    $1 == "reverse_proxy" && current != "" {
      if (current == "*") print "* " $2
      else { n = split(paths[current], p, " "); for (i = 1; i <= n; i++) print p[i] " " $2 }
    }
    $1 == "}" { current = "" }
  ' "$1"
}

# shape <Caddyfile>: what the site serves apart from its upstreams, one line per matcher's
# definition and per line inside a handle, whitespace collapsed, comments dropped, and each
# reverse_proxy's upstream left out, so two Caddyfiles that route and serve the same paths the same
# way print the same lines in the same order.
shape() {
  awk '
    /^[ \t]*#/ || /^[ \t]*$/ { next }
    { $1 = $1 }
    $1 ~ /^@/ && $2 == "path" { print "matcher " $0; next }
    $1 == "handle" { current = ($2 == "{") ? "*" : $2; next }
    current != "" && $1 == "}" { current = ""; next }
    current != "" && $1 == "reverse_proxy" { print current " reverse_proxy"; next }
    current != "" { print current " " $0 }
  ' "$1"
}

# commands <Dockerfile>: its lines with each backslash continuation joined, comments dropped, and
# split at && and ; into one command per line, by scripts/ci/dockerfile.sh.
commands() { bash "$repo/scripts/ci/dockerfile.sh" commands "$1"; }

# --- The checks. Each takes a root and prints one line per problem, nothing when sound. --------

# Each manifest entry is a compose service of the same name listening on the manifest's port, and
# each entry with a migrate has a <name>-migrate service in the migrate profile.
check_services() {
  local root="$1" compose="$1/infra/hosted/compose.yaml" name port migrate b
  while IFS=' ' read -r name port migrate; do
    [ -z "$name" ] && continue
    b="$(block "$compose" "$name")"
    if [ -z "$b" ]; then
      echo "$name: no compose service"
    elif ! grep -Eq "0\.0\.0\.0:$port([^0-9]|$)|\"--port\", \"$port\"" <<< "$b"; then
      echo "$name: does not listen on 0.0.0.0:$port"
    fi
    if [ "$migrate" = true ]; then
      b="$(block "$compose" "$name-migrate")"
      if [ -z "$b" ]; then
        echo "$name: no $name-migrate service"
      elif ! grep -Eq '^ +profiles: \["migrate"\]$' <<< "$b"; then
        echo "$name-migrate: not in the migrate profile alone"
      fi
    fi
  done < <(jq -r '.services[] | "\(.name) \(.port) \(has("migrate"))"' "$root/infra/services.json")
}

# The owner's URL is in each migrate service and in no serving one.
check_owner_url() {
  local root="$1" compose="$1/infra/hosted/compose.yaml" name var owner b
  while IFS=' ' read -r name var owner; do
    [ -z "$name" ] && continue
    grep -q "^ *$var:" <<< "$(block "$compose" "$name-migrate")" || echo "$name-migrate: no $var"
    b="$(block "$compose" "$name")"
    grep -Eq "$var|//$owner:" <<< "$b" && echo "$name: a serving service has the owner's URL"
  done < <(jq -r '.services[] | select(.database) | "\(.name) \(.database.owner_url) \(.database.owner)"' "$root/infra/services.json")
  return 0
}

# The hosted registry lists the dev registry's features in the same order, with the same name,
# prefix, display, icon and attention, and each upstream is http://<service>:<port> for the manifest entry on the
# dev upstream's port.
check_registry() {
  local root="$1" dev hosted i n d h dev_port service
  dev="$(features "$root/infra/features.toml")"
  hosted="$(features "$root/infra/hosted/features.toml")"
  [ -z "$dev" ] && echo "the dev registry has no features"
  n="$(printf '%s\n' "$dev" | grep -c .)"
  [ "$n" != "$(printf '%s\n' "$hosted" | grep -c .)" ] && echo "the registries have different numbers of features"
  i=1
  while [ "$i" -le "$n" ]; do
    d="$(printf '%s\n' "$dev" | sed -n "${i}p")"
    h="$(printf '%s\n' "$hosted" | sed -n "${i}p")"
    if [ "${d%|*}" != "${h%|*}" ]; then
      echo "feature $i: dev has ${d%|*}, hosted has ${h%|*}"
    else
      dev_port="$(printf '%s' "${d##*|}" | sed -nE 's#^https?://[^/:]+:([0-9]+)$#\1#p')"
      service="$(jq -r --argjson p "${dev_port:-0}" '.services[] | select(.port == $p) | .name' "$root/infra/services.json")"
      if [ -z "$service" ]; then
        echo "feature $i: no service on the dev upstream's port ${dev_port:-none}"
      elif [ "${h##*|}" != "http://$service:$dev_port" ]; then
        echo "feature $i: upstream ${h##*|}, expected http://$service:$dev_port"
      fi
    fi
    i=$((i + 1))
  done
}

# The hosted Caddyfile routes each path the dev one routes to the service on the same port, by its
# name, and serves the rest, the design paths among them, the same way. Each feature's prefix goes
# to its hosted upstream.
check_caddyfile() {
  local root="$1" dev hosted path upstream port service got prefix
  dev="$(routes "$root/infra/Caddyfile")"
  hosted="$(routes "$root/infra/hosted/Caddyfile")"
  [ -z "$dev" ] && echo "the dev Caddyfile routes nothing"
  while IFS=' ' read -r path upstream; do
    [ -z "$path" ] && continue
    port="${upstream##*:}"
    service="$(jq -r --argjson p "$port" '.services[] | select(.port == $p) | .name' "$root/infra/services.json" 2>/dev/null)"
    got="$(printf '%s\n' "$hosted" | awk -v p="$path" '$1 == p { print $2; exit }')"
    if [ -z "$service" ]; then
      echo "$path: no service on the dev upstream's port $port"
    elif [ "$got" != "$service:$port" ]; then
      echo "$path: hosted proxies to ${got:-nothing}, expected $service:$port"
    fi
  done <<< "$dev"
  [ "$(printf '%s\n' "$dev" | grep -c .)" != "$(printf '%s\n' "$hosted" | grep -c .)" ] &&
    echo "the hosted Caddyfile proxies a different number of paths"
  while IFS='|' read -r _ prefix _ _ _ upstream; do
    [ -z "$prefix" ] && continue
    got="$(printf '%s\n' "$hosted" | awk -v p="$prefix" '$1 == p { print $2; exit }')"
    [ "http://$got" != "$upstream" ] && echo "feature $prefix: hosted Caddyfile proxies to ${got:-nothing}, its upstream is $upstream"
  done <<< "$(features "$root/infra/hosted/features.toml")"
  if [ "$(shape "$root/infra/Caddyfile")" != "$(shape "$root/infra/hosted/Caddyfile")" ]; then
    echo "the hosted Caddyfile's matchers and handles differ from the dev one's: $(diff <(shape "$root/infra/Caddyfile") <(shape "$root/infra/hosted/Caddyfile") | grep '^[<>]' | tr '\n' ';')"
  fi
}

# The proxy's image holds every design path the hosted Caddyfile serves.
check_proxy_files() {
  local root="$1" dockerfile="$1/infra/hosted/proxy/Dockerfile" path copies
  copies="$(commands "$dockerfile" | awk '$1 == "COPY" { for (i = 2; i < NF; i++) if ($i !~ /^--/) print $i }')"
  for path in $(awk '$1 == "@design" && $2 == "path" { for (i = 3; i <= NF; i++) print $i }' "$root/infra/hosted/Caddyfile"); do
    path="${path#/}"
    path="${path%/\*}"
    grep -qx "$path" <<< "$copies" || echo "the proxy's Dockerfile does not copy $path"
  done
  grep -q 'gallery/dist$' <<< "$copies" || echo "the proxy's Dockerfile does not copy the gallery's build"
}

# Each variable the compose file reads is a line of .env.example, commented out or not.
check_env() {
  local root="$1" var
  for var in $(grep -o '\${[A-Za-z_][A-Za-z0-9_]*' "$root/infra/hosted/compose.yaml" | cut -c3- | sort -u); do
    grep -Eq "^(# )?$var=" "$root/infra/hosted/.env.example" || echo "$var is not in .env.example"
  done
}

# Every install in a Dockerfile is the locked one, by the rules in scripts/ci/dockerfile.sh, which
# scripts/ci/workflows-locked.test.sh also holds every Dockerfile to.
check_locked() {
  local root="$1" out
  out="$(cd "$root" && bash "$repo/scripts/ci/dockerfile.sh" unlocked Core/Dockerfile canary/Dockerfile \
    Brain/Dockerfile review/Dockerfile devflow/Dockerfile devflow/worker.Dockerfile infra/hosted/proxy/Dockerfile)"
  grep -v '^checked ' <<< "$out"
  [ "$(sed -n 's/^checked //p' <<< "$out")" = 0 ] && echo "no install found in any Dockerfile"
  return 0
}

# Each dev init script is mounted into the hosted Postgres's init directory under its own name, and
# the backup role's script last, since it grants CONNECT on the databases the others create. One
# mount per file, since a file cannot be mounted inside a folder mounted read only.
check_initdb() {
  local root="$1" b file name last
  b="$(block "$root/infra/hosted/compose.yaml" postgres)"
  for file in "$root"/infra/postgres/initdb/*; do
    [ -f "$file" ] || continue
    name="${file##*/}"
    grep -Fqx -- "      - ../postgres/initdb/$name:/docker-entrypoint-initdb.d/$name:ro" <<< "$b" ||
      echo "infra/postgres/initdb/$name is not mounted into the hosted Postgres"
  done
  grep -Fqx -- "      - ./postgres/backup-role.sh:/docker-entrypoint-initdb.d/zz-backup-role.sh:ro" <<< "$b" ||
    echo "postgres/backup-role.sh is not mounted as zz-backup-role.sh"
  last="$(grep -Eo '/docker-entrypoint-initdb\.d/[^:]+' <<< "$b" | LC_ALL=C sort | tail -n 1)"
  [ "$last" = /docker-entrypoint-initdb.d/zz-backup-role.sh ] ||
    echo "the last init script is ${last:-none}, not zz-backup-role.sh"
  return 0
}

# The compose file names no project, and platform-hosted is in it only as an image's prefix: each
# service's image, and Dev flow's DEVFLOW_WORKER_IMAGE, the worker's base image by name.
check_no_project() {
  local compose="$1/infra/hosted/compose.yaml" all images
  grep -q '^name:' "$compose" && echo "compose.yaml has a top level name"
  all="$(grep -o 'platform-hosted' "$compose" | grep -c .)"
  images="$(grep -Ec '^ +(image|DEVFLOW_WORKER_IMAGE): platform-hosted/[a-z0-9-]+:' "$compose")"
  [ "$images" -eq 0 ] && echo "no image is named platform-hosted/<service>"
  [ "$all" != "$images" ] && echo "platform-hosted appears $all times, $images of them as an image prefix"
  return 0
}

# run <case> <check>: the check on the repository's own files.
run() {
  local name="$1" got
  got="$("$2" "$repo" 2>&1)"
  if [ -z "$got" ]; then ok "$name"; else bad "$name" "$(printf '%s' "$got" | tr '\n' ';')"; fi
}

# expect <case> <check> <pattern> <file> <sed script>: the check on a copy of the files with one
# changed, expecting a problem matching <pattern>.
expect() {
  local name="$1" check="$2" pattern="$3" file="$4" script="$5" root f got
  root="$tmp/copy"
  rm -rf "$root"
  for f in $FILES; do
    mkdir -p "$root/$(dirname "$f")"
    [ -f "$repo/$f" ] && cp "$repo/$f" "$root/$f"
  done
  mkdir -p "$root/infra/postgres"
  cp -R "$repo/infra/postgres/initdb" "$root/infra/postgres/"
  sed -E "$script" "$root/$file" > "$root/$file.new" && mv "$root/$file.new" "$root/$file"
  got="$("$check" "$root" 2>&1)"
  if grep -qE "$pattern" <<< "$got"; then
    ok "refuses $name"
  else
    bad "refuses $name" "expected /$pattern/, got: $(printf '%s' "$got" | tr '\n' ';')"
  fi
}

run every_service_in_the_manifest_is_a_compose_service_on_its_port check_services
run no_serving_service_has_the_owners_url check_owner_url
run the_hosted_registry_matches_the_dev_registry check_registry
run the_hosted_caddyfile_routes_every_prefix_to_its_service check_caddyfile
run the_proxy_image_holds_every_design_path_it_serves check_proxy_files
run every_variable_the_compose_file_reads_is_in_the_example check_env
run no_dockerfile_installs_unlocked check_locked
run the_compose_file_names_no_project check_no_project
run every_init_script_is_mounted_and_the_backup_role_last check_initdb

expect "a service missing from compose" check_services "canary: no compose service" \
  infra/hosted/compose.yaml 's/^  canary:$/  kanary:/'
expect "a service on another port" check_services "brain: does not listen on 0.0.0.0:8082" \
  infra/hosted/compose.yaml 's/"--port", "8082"/"--port", "8092"/'
expect "a migrate service missing" check_services "review: no review-migrate service" \
  infra/hosted/compose.yaml 's/^  review-migrate:$/  review-migrates:/'
expect "a migrate service outside the profile" check_services "core-migrate: not in the migrate profile" \
  infra/hosted/compose.yaml 's/profiles: \["migrate"\]/profiles: ["tools"]/'
expect "a serving service with the owner's URL" check_owner_url "core: a serving service has the owner's URL" \
  infra/hosted/compose.yaml 's#//core_app:#//core_owner:#'
expect "a feature in another order" check_registry "feature 1: dev has Canary.*hosted has Review" \
  infra/hosted/features.toml 's/"Canary"/"X"/; s/"Review"/"Canary"/; s/"X"/"Review"/; s#"/canary#"/x#; s#"/review#"/canary#; s#"/x#"/review#'
expect "an upstream by address" check_registry "upstream http://127.0.0.1:8081, expected http://canary:8081" \
  infra/hosted/features.toml 's#http://canary:8081#http://127.0.0.1:8081#'
expect "a display that differs" check_registry "feature 2: dev has" \
  infra/hosted/features.toml 's#/review/api/display#/review/api/other#'
expect "an icon that differs" check_registry "feature 2: dev has.*\|layers\|, hosted has.*\|box\|" \
  infra/hosted/features.toml 's#"layers"#"box"#'
expect "an attention that differs" check_registry "feature 3: dev has.*\|/devflow/api/attention, hosted has.*\|/devflow/api/other" \
  infra/hosted/features.toml 's#/devflow/api/attention#/devflow/api/other#'
expect "a route to the wrong service" check_caddyfile "/review: hosted proxies to canary:8083, expected review:8083" \
  infra/hosted/Caddyfile 's/review:8083/canary:8083/'
expect "a catch all to another service" check_caddyfile '\*: hosted proxies to brain:8080' \
  infra/hosted/Caddyfile 's/core:8080/brain:8080/'
expect "a design path the dev Caddyfile does not serve" check_caddyfile "differ from the dev one" \
  infra/hosted/Caddyfile 's#/design/tokens.css #/design/tokens.css /design/DESIGN.md #'
expect "a design handle without no-cache" check_caddyfile "differ from the dev one" \
  infra/hosted/Caddyfile 's/header Cache-Control no-cache/header Cache-Control max-age=60/'
expect "a design file the proxy does not copy" check_proxy_files "does not copy design/nav.js" \
  infra/hosted/proxy/Dockerfile 's# design/nav.js##'
expect "a variable missing from .env.example" check_env "CORE_SIGNING_KEY is not in" \
  infra/hosted/.env.example 's/^# CORE_SIGNING_KEY=.*//'
expect "cargo build without --locked" check_locked "cargo build without --locked" \
  Core/Dockerfile 's/ --locked//'
expect "uv sync without --locked" check_locked "Brain/Dockerfile: uv sync without --locked" \
  Brain/Dockerfile 's/uv sync --locked/uv sync/'
expect "npm install" check_locked "canary/Dockerfile: npm installs other than npm ci" \
  canary/Dockerfile '$a\
RUN npm install'
expect "the worker's tools installed with npm install" check_locked "devflow/worker.Dockerfile: npm installs other than npm ci" \
  devflow/worker.Dockerfile 's/npm ci --omit=dev/npm install -g @anthropic-ai\/claude-code@2.1.325/'
expect "dotnet restore without --locked-mode" check_locked "dotnet restore without --locked-mode" \
  review/Dockerfile 's/ --locked-mode//'
expect "dotnet publish that restores again" check_locked "dotnet publish restores again" \
  review/Dockerfile 's/ --no-restore//'
expect "a dev init script not mounted" check_initdb "infra/postgres/initdb/brain.sh is not mounted" \
  infra/hosted/compose.yaml '\#/docker-entrypoint-initdb.d/brain.sh:ro#d'
expect "an init script after the backup role's" check_initdb "the last init script is .*zzz-late.sh" \
  infra/hosted/compose.yaml 's#(/docker-entrypoint-initdb.d/zz-backup-role.sh:ro)#\1\
      - ./x.sh:/docker-entrypoint-initdb.d/zzz-late.sh:ro#'
expect "a top level name" check_no_project "has a top level name" \
  infra/hosted/compose.yaml '1i\
name: hosted'
expect "the project's name in a comment" check_no_project "platform-hosted appears" \
  infra/hosted/compose.yaml '1i\
# docker compose -p platform-hosted up'

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
