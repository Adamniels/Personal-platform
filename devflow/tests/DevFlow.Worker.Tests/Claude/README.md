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
