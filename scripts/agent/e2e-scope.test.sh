#!/usr/bin/env bash
# Tests for e2e-scope.sh, against the real manifest and e2e/tests/ of this repository, so a service
# whose e2e globs stop matching any file fails here. Run: bash scripts/agent/e2e-scope.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pass=0 fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}
# scope <path>...: what the script prints for these paths, as one space separated line.
scope() { printf '%s\n' "$@" | bash "$here/e2e-scope.sh" | tr '\n' ' ' | sed 's/ $//'; }
has() { case " $1 " in *" $2 "*) return 0 ;; esac; return 1; }

out="$(scope e2e/tests/test_registration.py)"
check "a changed test file is run by itself" $([ "$out" = tests/test_registration.py ]; echo $?)

out="$(scope devflow/src/DevFlow/Program.cs)"
check "a service's change runs its own files and the shared ones" \
  $(has "$out" tests/test_devflow.py && has "$out" tests/test_devflow_look.py && has "$out" tests/test_look.py \
    && has "$out" tests/test_front_page.py && ! has "$out" tests/test_review.py && ! has "$out" all; echo $?)

out="$(scope devflow/web/src/sections.ts)"
check "a service's frontend runs its service's files" $(has "$out" tests/test_devflow.py && ! has "$out" all; echo $?)

out="$(scope Brain/src/brain/app.py review/src/Review/Program.cs)"
check "two services run both services' files, each once" \
  $(has "$out" tests/test_brain.py && has "$out" tests/test_memory_ask.py && has "$out" tests/test_review_study.py \
    && [ "$(tr ' ' '\n' <<<"$out" | sort | uniq -d)" = "" ]; echo $?)

out="$(scope devflow/src/DevFlow/Program.cs e2e/tests/test_registration.py)"
check "a changed test file is added to the service's files" \
  $(has "$out" tests/test_registration.py && has "$out" tests/test_devflow.py; echo $?)

for path in Core/src/main.rs e2e/conftest.py e2e/look.py design/tokens.css contracts/x.json infra/Caddyfile \
  libs/python/x/y.py .github/workflows/e2e.yml infra/services.json; do
  out="$(scope "$path")"
  [ "$out" = all ] || { echo "     $path gave: $out"; bad=1; }
done
check "the core, e2e's own helpers and what every page shares run the whole suite" $([ "${bad:-0}" = 0 ]; echo $?)

out="$(scope devflow/src/DevFlow/Program.cs Core/src/main.rs)"
check "all wins over any list" $([ "$out" = all ]; echo $?)

out="$(scope High-level/docs/agent-workflow.md Brain/docs/x.md design/reference/a.html scripts/agent/run.sh)"
check "paths that do not reach e2e print nothing" $([ -z "$out" ]; echo $?)

out="$(scope e2e/tests/test_gone.py)"
check "a deleted test file is left out" $([ -z "$out" ]; echo $?)

out="$(printf '' | bash "$here/e2e-scope.sh")"; rc=$?
check "no paths print nothing and exit 0" $([ "$rc" = 0 ] && [ -z "$out" ]; echo $?)

# Every glob a service declares matches at least one file, or a rename would silently drop its tests.
missing=""
while IFS= read -r glob; do
  [ -n "$glob" ] || continue
  # shellcheck disable=SC2086
  ls $here/../../e2e/$glob > /dev/null 2>&1 || missing="$missing $glob"
done < <(jq -r '.services[] | .e2e // empty | .[]' "$here/../../infra/services.json")
check "every service's e2e glob matches a file" $([ -z "$missing" ] || echo "     no file:$missing" >&2; [ -z "$missing" ]; echo $?)

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
