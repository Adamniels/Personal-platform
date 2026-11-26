#!/usr/bin/env bash
# Proposes the next slice for `/pick-up-slice` without a number, from Project 5.
#
#   bash scripts/agent/next.sh [--number | --machine | --blocked]
#
# Prints four sections: "In flight" (In progress, In review or Blocked), "Proposal" (one slice),
# "Alternatives" (up to two) and "Blocked" (Ready slices waiting for an open blocker, with the
# blocker). Only open issues with status Ready are candidates: Ready means planned and free for an
# agent, see High-level/docs/agent-workflow.md. A candidate is blocked while any issue in its
# GitHub "blocked by" list is open. Candidates are ranked by their position among their parent's
# sub-issues, then by issue number; one without a parent comes after every one with a
# parent. Nothing is written.
# With --number, prints only the proposed slice's number, or nothing when there is none; for scripts.
# With --machine, one board read printing `inprogress <n>` for every open issue with status In progress and
# `next <n>` for the proposal (absent when there is none); pending.sh relies on this format.
# With --blocked, `<n> <blockers>` for every Ready slice waiting for an open blocker, in rank order, the blockers
# as `#<n>` joined by commas, so the runner's idle mention can say what holds the queue back.
# Exit codes: 0 ok, non-zero when the GitHub read fails (nothing is proposed then), 2 usage error.
set -euo pipefail

OWNER="Adamniels"
REPO_NAME="Personal-platform"
PROJECT_NUMBER=5

mode=human
case "${1:-}" in
  "") ;;
  --number) mode=number ;;
  --machine) mode=machine ;;
  --blocked) mode=blocked ;;
  *) echo "usage: next.sh [--number | --machine | --blocked]" >&2; exit 2 ;;
esac
[ $# -le 1 ] || { echo "usage: next.sh [--number | --machine | --blocked]" >&2; exit 2; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# With --paginate, gh prints one JSON document per page; jq --slurp reads them all.
gh api graphql --paginate \
  -f query="query(\$endCursor:String){user(login:\"$OWNER\"){projectV2(number:$PROJECT_NUMBER){items(first:100,after:\$endCursor){pageInfo{hasNextPage endCursor} nodes{fieldValueByName(name:\"Status\"){... on ProjectV2ItemFieldSingleSelectValue{name}} content{... on Issue{number title state repository{nameWithOwner} parent{number subIssues(first:100){nodes{number}}} blockedBy(first:50){nodes{number state}}}}}}}}}" \
  > "$tmp/board.json"

jq -r --slurp --arg mode "$mode" --arg repo "$OWNER/$REPO_NAME" '
  [.[] | .data.user.projectV2.items.nodes[]
    | select(.content.repository.nameWithOwner? == $repo and .content.state == "OPEN")
    | .content + {status: (.fieldValueByName.name // "none")}] as $open

  | def rank:
      if .parent == null then [999999, 0, .number]
      else .number as $n
        | [.parent.number, ([.parent.subIssues.nodes[].number] | index($n) // 999999), $n]
      end;
    def blockers: [.blockedBy.nodes[] | select(.state == "OPEN") | "#\(.number)"];
    def line: "  #\(.number) \(.title)\(if .parent then " (under #\(.parent.number))" else "" end)";
    def lines: if length == 0 then "  none" else .[] end;

  [$open[] | select(.status == "In progress" or .status == "In review" or .status == "Blocked")] as $inflight
  | [$open[] | select(.status == "Ready") | . + {key: rank}] | sort_by(.key) as $ready
  | [$ready[] | select(blockers == [])] as $free
  | [$ready[] | select(blockers != [])] as $blocked

  | if $mode == "number" then ($free[0:1][] | .number)
    elif $mode == "machine" then
      ($open | sort_by(.number)[] | select(.status == "In progress") | "inprogress \(.number)"), ($free[0:1][] | "next \(.number)")
    elif $mode == "blocked" then ($blocked[] | "\(.number) \(blockers | join(","))")
    else
    "In flight:", ($inflight | sort_by(.number) | map("  #\(.number) \(.status): \(.title)") | lines),
    "Proposal:", ($free[0:1] | map(line) | lines),
    "Alternatives:", ($free[1:3] | map(line) | lines),
    "Blocked:", ($blocked | map("\(line): waits for \(blockers | join(", "))") | lines)
    end
' "$tmp/board.json"
