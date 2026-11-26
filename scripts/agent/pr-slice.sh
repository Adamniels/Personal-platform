#!/usr/bin/env bash
# Which slice a pull request closes: the one rule the merge gate and the runner share.
#
#   bash scripts/agent/pr-slice.sh < pr.json
#
# Reads one pull request as JSON on stdin, with headRefName, body and closingIssuesReferences (each with number
# and repository{name, owner{login}}), as `gh pr view` and `gh pr list` give them, and prints one line:
#   <n> linked   GitHub links the pull request to exactly issue <n> of this repository, and the head branch starts
#                with `<n>-`. GitHub closes <n> when the pull request merges.
#   <n> body     GitHub links no issue, the head branch starts with `<n>-`, and the body closes exactly <n>: a line
#                starting with a closing keyword (close, closes, closed, fix, fixes, fixed, resolve, resolves,
#                resolved, any case, an optional colon) and `#<n>` or `Adamniels/Personal-platform#<n>`, outside a
#                code fence. GitHub missed the link, which happened to #471 on 2026-11-25, so GitHub closes
#                nothing: whoever settles the merge closes <n>.
#   -            anything else: no number on the branch, a link to another issue, repository or several issues, or
#                a body that closes none, another or several.
# Exit codes: 0 with the line, 1 when the input is not such a pull request.
set -euo pipefail

jq -r --arg owner "Adamniels" --arg repo "Personal-platform" '
  if type != "object" or (.headRefName | type) != "string" or (.closingIssuesReferences | type) != "array"
     or ((.body // "") | type) != "string"
  then error("not a pull request") else . end
  | ((.headRefName | capture("^(?<n>[1-9][0-9]*)-").n) // null) as $branch
  | .closingIssuesReferences as $refs
  # The numbers the body closes, outside fenced code. A fence opens with 3 or more ` or ~ and closes on a line of
  # the same character, at least as long, with nothing after it.
  | [ foreach ((.body // "") | gsub("\r"; "") | split("\n"))[] as $line ({fence: null, n: null};
        ($line | sub("^\\s+"; "")) as $t
        | (($t | capture("^(?<run>`{3,}|~{3,})(?<rest>.*)$")) // null) as $f
        | if .fence != null then
            (if $f != null and ($f.run[0:1] == .fence[0:1]) and ($f.run | length) >= (.fence | length)
                and ($f.rest | test("^\\s*$")) then .fence = null else . end) | .n = null
          elif $f != null then .fence = $f.run | .n = null
          else .n = (($t | capture("^(?i:close[sd]?|fix(?:e[sd])?|resolve[sd]?):?\\s+(?:Adamniels/Personal-platform)?#(?<n>[0-9]+)\\b").n) // null)
          end;
        .n | select(. != null)) ] | unique as $closes
  | if $branch == null then "-"
    elif ($refs | length) > 0 then
      (if ($refs | length) == 1 and $refs[0].repository.owner.login == $owner and $refs[0].repository.name == $repo
          and ($refs[0].number | tostring) == $branch then "\($branch) linked" else "-" end)
    elif $closes == [$branch] then "\($branch) body"
    else "-" end
' || { echo "pr-slice.sh: the input is not a pull request" >&2; exit 1; }
