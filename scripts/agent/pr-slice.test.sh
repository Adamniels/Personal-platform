#!/usr/bin/env bash
# Offline tests for pr-slice.sh: canned pull requests on stdin, no GitHub.
# Run: bash scripts/agent/pr-slice.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pass=0 fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}
# pr <branch> <body> [linked numbers, comma separated] [repository] [owner]: prints the slice line.
pr() {
  jq -n -c --arg b "$1" --arg body "$2" --arg refs "${3:-}" --arg repo "${4:-Personal-platform}" --arg owner "${5:-Adamniels}" \
    '{headRefName: $b, body: $body, closingIssuesReferences: [$refs | split(",")[] | select(. != "") |
      {number: tonumber, repository: {name: $repo, owner: {login: $owner}}}]}' | bash "$here/pr-slice.sh"
}
is() { [ "$1" = "$2" ] || { echo "     got: $1, want: $2"; return 1; }; }

summary="## Summary

Some text.

Closes #442

## Tasks"

is "$(pr 442-x "$summary" 442)" "442 linked"; check "GitHub's link to the branch's issue is linked" $?
is "$(pr 442-x "$summary")" "442 body"; check "no link and the body closes the branch's issue is body" $?
is "$(pr 442-x "Fixes: #442")" "442 body"; check "a keyword with a colon counts" $?
is "$(pr 442-x "RESOLVED Adamniels/Personal-platform#442")" "442 body"; check "a keyword in any case, with the repository, counts" $?
is "$(pr 442-x "closes #442"$'\r'"")" "442 body"; check "a CRLF body counts" $?
is "$(pr 442-x "Closes #442
Closes #442")" "442 body"; check "the same issue twice is still one" $?
is "$(pr 442-x "Closes #443")" "-"; check "a body closing another issue is no slice" $?
is "$(pr 442-x "Closes #442
Fixes #443")" "-"; check "a body closing two issues is no slice" $?
is "$(pr 442-x "Some text, no keyword.")" "-"; check "a body closing nothing is no slice" $?
is "$(pr 442-x "This closes #442 in passing")" "-"; check "a keyword not at the start of a line does not count" $?
is "$(pr 442-x "Closes #4420")" "-"; check "a longer number is another issue" $?
is "$(pr 442-x "Closes Other/repo#442")" "-"; check "another repository's issue does not count" $?
is "$(pr 442-x '```
Closes #442
```')" "-"; check "a keyword inside a code fence does not count" $?
is "$(pr 442-x '````
```
Closes #442
````
Closes #442')" "442 body"; check "a fence closes only on its own length, and the line after it counts" $?
is "$(pr 442-x "$summary" 443)" "-"; check "a link to another issue is no slice, whatever the body says" $?
is "$(pr 442-x "$summary" 442,443)" "-"; check "links to two issues are no slice" $?
is "$(pr 442-x "$summary" 442 Other)" "-"; check "a link to another repository is no slice" $?
is "$(pr 442-x "$summary" 442 Personal-platform someone)" "-"; check "a link to another owner is no slice" $?
is "$(pr fix-something "Closes #442")" "-"; check "a branch without a number is no slice" $?
is "$(pr 0442-x "Closes #442")" "-"; check "a branch number with a leading zero is no slice" $?
is "$(jq -n -c '{headRefName: "442-x", body: null, closingIssuesReferences: []}' | bash "$here/pr-slice.sh")" "-"
check "a missing body is no slice" $?
echo '[]' | bash "$here/pr-slice.sh" > /dev/null 2>&1; [ $? = 1 ]; check "input that is not a pull request exits 1" $?
echo '{"headRefName":"442-x"}' | bash "$here/pr-slice.sh" > /dev/null 2>&1; [ $? = 1 ]
check "a pull request without its closing references exits 1" $?

echo "pr-slice.test.sh: $pass passed, $fail failed"
[ "$fail" = 0 ]
