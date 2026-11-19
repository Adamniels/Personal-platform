# Claude Code's answers

What `claude -p … --output-format json` and `claude auth status --json` answer, one file per case,
read by `ClaudeAgentTests` in place of running Claude Code. Each is the standard output alone,
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
