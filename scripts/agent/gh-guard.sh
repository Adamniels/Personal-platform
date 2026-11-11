#!/usr/bin/env bash
# A `gh` wrapper for implementer runs. run.sh puts a copy of this file, named `gh`, first on PATH for
# `/pick-up-slice` runs only, so an implementer cannot post or edit a verdict, edit or delete a comment, or merge,
# even though it shares the bot identity with the reviewer (rulings R25, R36). The deny rules in
# .claude/settings.json and run.sh's --disallowedTools patterns guard the same ground; this is the third layer.
#
#   gh <args...>        exactly as the real gh
#
# Refused, with exit 1 and a "gh-guard: refused: ..." line on stderr, before the real gh runs:
#   pr merge; any alias or extension command (they would run a refused command under another name)
#   pr comment / issue comment with --edit-last or --delete-last
#   pr comment, issue comment, pr edit, issue edit whose body (-b, --body, -F, --body-file, `-` for stdin) has a
#     first non-empty line that starts, after spaces, with `<!--`: the shape of a verdict marker
#   api with any field (-f, -F, --raw-field, --field; `@file` and `@-` read) whose value's first non-empty line
#     starts with `<!--`, or any string in an --input JSON file that does (an unreadable --input is refused)
#   api with method PATCH or DELETE (-X, --method, any case) when any argument mentions `comments`
#   api on a `pulls/<n>/merge` path, with any method
#   api graphql whose query contains `<!--` or the mutations mergePullRequest, enablePullRequestAutoMerge,
#     updateIssueComment or deleteIssueComment
# The command and subcommand are the first two words that are not flags or flag values: `gh pr -R x merge` and
# `gh -R x pr merge` are pr merge. `-b=<v>`, `-F=<v>`, `-f=<v>` and `-X=<v>` are read as `-b <v>` and so on.
# Everything else execs the real gh with the same arguments and stdin (stdin is replayed when it was read).
# The real gh is the first `gh` on PATH that is not this wrapper: its own directory and any file that is the same
# file as this one are skipped.
# Exit codes: the real gh's, 1 when refused, 127 when no real gh is on PATH.
set -uo pipefail
set -f   # PATH entries and arguments are never globbed

self="$0"
self_dir="$(cd "$(dirname "$0")" && pwd -P)"
stdin_copy=""

refuse() { echo "gh-guard: refused: $*" >&2; exit 1; }

# A text is marker shaped when its first non-empty line, with leading spaces and tabs removed, starts with `<!--`.
marker_shaped() {
  local line
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line#"${line%%[![:space:]]*}"}"
    [ -z "$line" ] && continue
    case "$line" in '<!--'*) return 0 ;; esac
    return 1
  done <<<"$1"
  return 1
}

# Stdin is read once, into a file, the first time a body or field names `-`; the real gh then reads that file.
capture_stdin() {
  if [ -z "$stdin_copy" ]; then
    stdin_copy="$(mktemp)" || refuse "could not keep stdin"
    cat > "$stdin_copy"
  fi
}
# source_text <file|->: the content a body file or `@file` field names.
source_text() {
  if [ "$1" = - ]; then cat "$stdin_copy"; else cat -- "$1" 2>/dev/null; fi
}
# check_source <what> <file|->
check_source() {
  [ "$2" = - ] && capture_stdin
  marker_shaped "$(source_text "$2")" && refuse "$1 starts with <!--"
  return 0
}
check_text() { marker_shaped "$2" && refuse "$1 starts with <!--"; return 0; }

# Bodies of pr/issue comment and edit.
check_body_flags() {
  local a v
  while [ $# -gt 0 ]; do
    a="$1"; shift
    case "$a" in
      --edit-last|--delete-last) refuse "$a: comments are never edited or deleted" ;;
      -b|--body) [ $# -gt 0 ] && { check_text body "$1"; shift; } ;;
      --body=*) check_text body "${a#--body=}" ;;
      -b?*) v="${a#-b}"; check_text body "${v#=}" ;;
      -F|--body-file) [ $# -gt 0 ] && { check_source "body file" "$1"; shift; } ;;
      --body-file=*) check_source "body file" "${a#--body-file=}" ;;
      -F?*) v="${a#-F}"; check_source "body file" "${v#=}" ;;
    esac
  done
}

check_query() {
  case "$1" in *'<!--'*) refuse "graphql query contains <!--" ;; esac
  if grep -Eq 'mergePullRequest|enablePullRequestAutoMerge|updateIssueComment|deleteIssueComment' <<<"$1"; then
    refuse "graphql mutation that merges or edits comments"
  fi
}

# check_field <flag> <key=value>: -F and --field read `@file` (and `@-`); -f and --raw-field are literal.
check_field() {
  local flag="$1" kv="$2" key value
  key="${kv%%=*}"
  case "$kv" in *=*) value="${kv#*=}" ;; *) value="" ;; esac
  if { [ "$flag" = -F ] || [ "$flag" = --field ]; } && [ "${value:0:1}" = @ ]; then
    [ "${value#@}" = - ] && capture_stdin
    value="$(source_text "${value#@}")"
  fi
  check_text "field $key" "$value"
  [ "$key" = query ] && check_query "$value"
  return 0
}

check_input() {
  local file="$1" strings
  [ "$file" = - ] && capture_stdin
  command -v jq >/dev/null 2>&1 || refuse "--input needs jq to be checked"
  strings="$(source_text "$file" | jq -r '.. | strings | @json')" || refuse "--input is not readable JSON"
  local s
  while IFS= read -r s; do
    [ -n "$s" ] || continue
    s="$(jq -r . <<<"$s")" || refuse "--input is not readable JSON"
    check_text "--input string" "$s"
    check_query "$s"
  done <<<"$strings"
}

check_api() {
  local a v method="" all=" $* "
  while [ $# -gt 0 ]; do
    a="$1"; shift
    case "$a" in
      -X|--method) [ $# -gt 0 ] && { method="$1"; shift; } ;;
      --method=*) method="${a#--method=}" ;;
      -X?*) method="${a#-X}"; method="${method#=}" ;;
      -f|-F|--raw-field|--field) [ $# -gt 0 ] && { check_field "$a" "$1"; shift; } ;;
      --raw-field=*) check_field --raw-field "${a#--raw-field=}" ;;
      --field=*) check_field --field "${a#--field=}" ;;
      -f?*|-F?*) v="${a:2}"; check_field "${a:0:2}" "${v#=}" ;;
      --input) [ $# -gt 0 ] && { check_input "$1"; shift; } ;;
      --input=*) check_input "${a#--input=}" ;;
    esac
  done
  method="$(tr '[:lower:]' '[:upper:]' <<<"$method")"
  if [ "$method" = PATCH ] || [ "$method" = DELETE ]; then
    case "$all" in *comments*) refuse "$method on comments" ;; esac
  fi
  if grep -Eiq '(^|[ /])pulls/[^/ ]+/merge([ /?]|$)' <<<"$all"; then refuse "the pull request merge endpoint"; fi
  return 0
}

real_gh() {
  local d dp IFS=:
  for d in $PATH; do
    [ -n "$d" ] || continue
    dp="$(cd "$d" 2>/dev/null && pwd -P)" || continue
    [ "$dp" = "$self_dir" ] && continue
    [ -f "$d/gh" ] && [ -x "$d/gh" ] || continue
    [ "$d/gh" -ef "$self" ] && continue
    echo "$d/gh"; return 0
  done
  return 1
}

# Flags that take a value as the next argument, wherever they stand; any other `-x` is treated as a switch.
VALUE_FLAGS=" -R --repo --hostname -X --method -H --header -f --raw-field -F --field -q --jq -t --template --cache -p --preview --input -b --body --body-file "
cmd="" sub="" skip=0
for a in "$@"; do
  if [ "$skip" = 1 ]; then skip=0; continue; fi
  case "$a" in
    --*=*) ;;
    -*) case "$VALUE_FLAGS" in *" $a "*) skip=1 ;; esac ;;
    *) if [ -z "$cmd" ]; then cmd="$a"; else sub="$a"; break; fi ;;
  esac
done

# Bodies, fields and methods are checked across every argument, so their position does not matter.
case "$cmd" in
  pr|issue)
    case "$sub" in
      merge) [ "$cmd" = pr ] && refuse "pr merge: only the merge gate merges" ;;
      comment|edit) check_body_flags "$@" ;;
    esac ;;
  api) check_api "$@" ;;
  alias|extension|extensions|ext) refuse "$cmd commands" ;;
esac

real="$(real_gh)" || { echo "gh-guard: no real gh on PATH" >&2; exit 127; }
if [ -n "$stdin_copy" ]; then
  # Replay the stdin that was read, and remove the file once the real gh holds it open.
  exec 0<"$stdin_copy"
  rm -f "$stdin_copy"
fi
exec "$real" "$@"
