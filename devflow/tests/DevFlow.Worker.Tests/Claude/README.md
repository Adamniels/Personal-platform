# Claude Code's answers

What `claude -p … --output-format json` and `claude auth status --json` answer, one file per case,
read by `ClaudeAgentTests` in place of running Claude Code; and in `stream/`, what each run prints
as a stream, see "The stream" below. Each is the standard output alone,
`.txt` where it is not JSON; the exit code is in the test beside its name.

They are written from what the experiment of 2026-11-16 saw on the Mac mini with Claude Code
2.1.325, as the slice "The worker program" records it, since the experiment's own files are not in
this repository. `usage-limit.json`, `no-report.json`, `report-not-an-object.json`,
`not-json.txt` and `success-with-is-error.json` are cases the rule must also hold for, written in
the same shape; the experiment did not record them. When Claude Code's version in the base image
changes, a real run of each case is the way to check them again.

`usage-limit-resets-today.json` and `usage-limit-resets-date.json` are a usage limit whose `result`
says when it resets, the two ways Claude Code writes it, read from Claude Code 2.1.322's own
formatter on 2026-11-19 rather than from a run at the limit: `resets 3pm (UTC)`, the hour, and the
minutes when not on the hour, in the zone named, for a reset within 24 hours; and
`resets Jan 20, 3:30pm (Europe/Stockholm)`, with the date, for one further off, the year added when
it is another year. The exact time, `resetsAt`, goes out only in a streamed `rate_limit_event`,
which `--output-format json` never prints, so the text is what the worker reads. The words before
`·` are not read. The base image pins 2.1.325, so a real run at the limit with it is the way to
check them again; `usage-limit.json` stays the case whose answer gives no time this reads.

## The stream

The worker runs Claude Code with `--output-format stream-json --verbose`, see "The agent interface"
in `devflow/README.md`, and `stream/<case>.jsonl` is what such a run prints, one JSON event per
line, for each run answer above that is JSON: every file here but the `auth-*.json` answers of the
login check and the two `.txt` cases. Each ends with a `result` event that is its twin's answer as
it is, on one line, since the `result` event carries exactly what `--output-format json` printed;
`ClaudeAgentTests.EveryRecordedCaseJudgesAsBefore` judges each stream against its twin.

None of them is a real run. The slice planned `success` and `max-turns` as real runs of the base
image's Claude Code, 2.1.325, with the agent's own login on the Mac mini, but on 2026-11-25 that
login could not be reached outside the agent's own session: `claude auth status --json` answered
`"loggedIn": false`, and a run answered only "this login reached its time limit and cannot be
renewed". That run did print a real 2.1.325 stream, and every case is written in its shape: a
`system` `init` event first, then `assistant` events whose `message.content` holds one block each,
`text` or `tool_use`, with the message's `usage`; `user` events holding a `tool_result` block, its
`content` a string or a list of `text` blocks; and the `result` event last. Its `init` event, which
named the machine's paths and the session's tools, is not kept; the `init` here is cut to the
fields a worker's run has. Claude Code answers an API error with an `assistant` event of its own,
`"model": "<synthetic>"` and `"is_api_error_message": true`, holding the error's text, as that run
did; the API error cases have it.

What that run did not show is written from Claude Code's documented event types: a message of two
blocks comes as two `assistant` events sharing its `id` and its `usage`, which `success.jsonl` has;
the report comes as a call of the `StructuredOutput` tool; and `rate_limit_event` carries
`rate_limit_info`, with `status`, `allowed`, `allowed_warning` or `rejected`, and `resetsAt`, a Unix
time in seconds. `success.jsonl` has one that is `allowed`, which says nothing of a failure. The
running usage in `success.jsonl` adds up to its `result` event's.

A real run of `success` and of `max-turns`, recorded with the agent's own setup token in
`CLAUDE_CODE_OAUTH_TOKEN` as a worker runs, is the way to check these, and the way to refresh them
when the base image's Claude Code changes.
