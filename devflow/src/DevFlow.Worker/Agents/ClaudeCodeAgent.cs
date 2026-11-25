using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Worker.Pages;

namespace DevFlow.Worker.Agents;

/// <summary>Where Claude Code is, the user's home it runs in, and where the image's Chromium is,
/// which the pictures the developer takes of its own pages need; the rest of its environment is
/// built from these and nothing else. The model is not among them: each run takes its job's.</summary>
internal sealed record ClaudeCodeOptions(string Executable, string Home, string Path, string Lang, string BrowsersPath)
{
    public static ClaudeCodeOptions Default { get; } = new("claude", "/home/agent", "/usr/local/bin:/usr/bin:/bin", "C.UTF-8", PageTools.ImageBrowsers);
}

/// <summary>
/// Claude Code run headless, as the experiment of 2026-11-16 found it must be. See "The agent
/// interface" in devflow/README.md, and "Workers" in devflow/docs/devflow-architecture.md for why.
/// <list type="bullet">
/// <item>The environment is built from nothing: <c>HOME</c>, <c>PATH</c>, <c>LANG</c>,
/// <c>CLAUDE_CONFIG_DIR</c> in that home, <c>CLAUDE_CODE_OAUTH_TOKEN</c>, and
/// <c>PLAYWRIGHT_BROWSERS_PATH</c>, the image's Chromium, for the developer's own pictures; the run,
/// not the login check, gets the project's values its job carried too, beneath these. An
/// <c>ANTHROPIC_API_KEY</c> would silently win over the token and bill the API, so none is ever
/// passed; nor is <c>--bare</c>, which refuses the token.</item>
/// <item>The login is checked first, with <c>claude auth status --json</c>, which must answer
/// <c>loggedIn</c> true and <c>authMethod</c> <c>oauth_token</c>; the method is recorded.</item>
/// <item>It streams, <c>--output-format stream-json --verbose</c>, one JSON event per line as it
/// works, and its answer is the last <c>result</c> event, which carries what
/// <c>--output-format json</c> printed.</item>
/// <item>A run succeeds only when every condition of <see cref="JudgeAnswer"/> holds; <c>subtype</c>
/// is never read, since it says <c>success</c> on some failures.</item>
/// <item>The token's value is replaced in everything read back, so neither the report, stderr nor
/// a reason can carry it out.</item>
/// </list>
/// </summary>
internal sealed class ClaudeCodeAgent(IProcessStarter starter, ClaudeCodeOptions options, WorkerLog log, TimeProvider? time = null) : IAgent
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The token's environment variable.</summary>
    public const string TokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary>The longest the login check may take.</summary>
    public static readonly TimeSpan LoginCheckLimit = TimeSpan.FromMinutes(1);

    /// <summary>What stands in for the token wherever it was written.</summary>
    public const string Redacted = "[redacted]";

    public async Task<AgentResult> Run(AgentRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var token = run.Credential.Reveal();
        var environment = Environment(token, null);

        var login = Scrubbed(await starter.Run(
            new ProcessSpec(options.Executable, ["auth", "status", "--json"], run.WorkingDirectory, environment),
            LoginCheckLimit,
            cancellationToken), token);
        var (loggedIn, method) = ReadLogin(login.Stdout);
        if (login.TimedOut || login.ExitCode != 0 || !loggedIn || method != "oauth_token")
        {
            log.Write($"Claude Code is not logged in with the token (exit {login.ExitCode}, method {method ?? "unknown"}); the run did not start.");
            return AgentResult.Failed(
                FailureSide.OutsideCard,
                $"Claude Code is not logged in with the token: auth status exited {login.ExitCode}, loggedIn {(loggedIn ? "true" : "false")}, authMethod {method ?? "unknown"}",
                [],
                login.Stderr,
                method);
        }

        log.Write($"Claude Code is logged in by {method}; starting the run.");
        // Each line of the stream becomes its log line as it is written; standard error is kept
        // for the result, as before, and makes none.
        var lines = run.Log is null ? null : new StreamLines(run.Log, token, new DevFlow.Protocol.ValueScrub(run.Values ?? []));
        var answer = Scrubbed(await starter.Run(
            new ProcessSpec(
                options.Executable,
                Arguments(run),
                run.WorkingDirectory,
                Environment(token, run.Values),
                Lines: lines is null ? null : (stream, line) =>
                {
                    if (stream == ProcessStream.Stdout)
                    {
                        lines.Read(line);
                    }
                }),
            run.Limits.Time,
            cancellationToken), token);
        var result = Judge(answer, run.Limits, method, run.Access, _time.GetUtcNow());
        log.Write(result.Failure is { } failure
            ? $"The run failed, {(failure.Side == FailureSide.Card ? "in the card" : "outside the card")}: {failure.Reason}."
            : "The run succeeded with a report.");
        return result;
    }

    /// <summary>The environment of a Claude Code process: the project's <paramref name="values"/>,
    /// for the run that works on its code, then Claude Code's own, which win over a value of the
    /// same name; the login check gets none.</summary>
    private Dictionary<string, string> Environment(string token, IReadOnlyList<DevFlow.Protocol.JobValue>? values)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            environment[value.Name] = value.Value;
        }

        environment["HOME"] = options.Home;
        environment["PATH"] = options.Path;
        environment["LANG"] = options.Lang;
        environment["CLAUDE_CONFIG_DIR"] = $"{options.Home.TrimEnd('/')}/.claude";
        environment[TokenVariable] = token;
        environment["PLAYWRIGHT_BROWSERS_PATH"] = options.BrowsersPath;
        return environment;
    }

    /// <summary>
    /// The run's arguments, with the model, the turns and the spend its job carried. A developer may
    /// change its copy and run commands, with
    /// <c>bypassPermissions</c>. A reviewer reads and nothing else: the default permission mode,
    /// which asks before any other tool, no prompts, so every ask is refused and reported in
    /// <c>permission_denials</c> rather than waiting, and only <c>Read</c>, <c>Grep</c> and
    /// <c>Glob</c> allowed, as the experiment of 2026-11-16 found.
    /// </summary>
    internal static List<string> Arguments(AgentRun run) =>
    [
        "-p", run.Brief,
        "--model", run.Model,
        .. run.Access == AgentAccess.ReadOnly
            ? (string[])["--permission-mode", "default", "--permission-prompts", "none", "--allowedTools", "Read,Grep,Glob"]
            : ["--permission-mode", "bypassPermissions"],
        "--output-format", "stream-json", "--verbose",
        "--json-schema", run.ReportSchema,
        "--max-turns", run.Limits.MaxTurns.ToString(CultureInfo.InvariantCulture),
        "--max-budget-usd", run.Limits.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// The run's stream judged: past the time limit, a failure in the card; a stream with no
    /// <c>result</c> event, nothing on standard output among them, a failure outside it; otherwise
    /// its last <c>result</c> event, which carries what <c>--output-format json</c> printed, by
    /// <see cref="JudgeAnswer"/>, with the reset of the last <c>rate_limit_event</c> that stopped
    /// the run, when one did. A line that is not a JSON object is not an event, and is passed over.
    /// </summary>
    internal static AgentResult Judge(ProcessOutcome outcome, AgentLimits limits, string? method, AgentAccess access = AgentAccess.Write, DateTimeOffset? now = null)
    {
        if (outcome.TimedOut)
        {
            return AgentResult.TimedOut(limits, [], outcome.Stderr, method);
        }

        JsonObject? result = null;
        DateTimeOffset? resetsAt = null;
        foreach (var line in outcome.Stdout.Split('\n'))
        {
            if (Event(line) is not { } @event)
            {
                continue;
            }

            switch (Text(@event, "type"))
            {
                case "result":
                    result = @event;
                    break;
                case "rate_limit_event" when UsageLimit.ResetsAt(@event) is { } resets:
                    resetsAt = resets;
                    break;
            }
        }

        return result is null
            ? AgentResult.Failed(FailureSide.OutsideCard, $"Claude Code ended without a result (exit {outcome.ExitCode})", [], outcome.Stderr, method)
            : JudgeAnswer(result, outcome.ExitCode, outcome.Stderr, limits, method, access, now ?? DateTimeOffset.UtcNow, resetsAt);
    }

    /// <summary><paramref name="line"/> as one of the stream's events, a JSON object, or null when
    /// it is not one.</summary>
    internal static JsonObject? Event(string line)
    {
        if (line.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                return JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// The rule, in order, the first that applies deciding, on Claude Code's answer, the
    /// <c>result</c> event, and the process's <paramref name="exitCode"/>. A success needs all of:
    /// exit 0; <c>is_error</c> false; <c>terminal_reason</c> <c>completed</c>;
    /// <c>structured_output</c> present and an object; <c>permission_denials</c> empty. Outside the
    /// card: an <c>api_error</c> of any kind (not logged in, a refused key, a usage limit, with when
    /// it resets: <paramref name="resetsAt"/>, from the stream's <c>rate_limit_event</c>, when it
    /// gave one, or else what its <c>result</c> says, read by <see cref="UsageLimit"/> at
    /// <paramref name="now"/>), and anything else Claude Code reports as an error. In the card:
    /// <c>max_turns</c>, <c>budget_exhausted</c>, refused actions and no valid report. For a read
    /// only run, a reviewer's, refused actions do not fail it, since it cannot write by
    /// construction; either way they are kept in the result as Claude Code listed them.
    /// </summary>
    internal static AgentResult JudgeAnswer(JsonObject output, int exitCode, string stderr, AgentLimits limits, string? method, AgentAccess access, DateTimeOffset now, DateTimeOffset? resetsAt = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        JsonArray? denied = null;
        AgentResult Failed(FailureSide side, string reason, JsonObject? usage = null) =>
            AgentResult.Failed(side, reason, usage ?? [], stderr, method, denied);

        var usage = Usage(output);
        var terminal = Text(output, "terminal_reason");
        var denials = output["permission_denials"] as JsonArray;
        denied = denials?.DeepClone().AsArray();
        switch (terminal)
        {
            case "api_error":
                var status = output["api_error_status"] is JsonValue code && code.TryGetValue<int>(out var number) ? $" {number}" : "";
                if ((resetsAt ?? UsageLimit.ResetsAt(Text(output, "result"), now)) is { } resets)
                {
                    return new AgentResult(
                        null,
                        new AgentFailure(FailureSide.OutsideCard, $"Claude's API answered an error{status}: the usage limit resets at {resets.UtcDateTime:yyyy-MM-dd HH:mm} UTC", ResetsAt: resets),
                        usage,
                        stderr,
                        method,
                        denied);
                }

                return Failed(FailureSide.OutsideCard, $"Claude's API answered an error{status}", usage);
            case "max_turns":
                return Failed(FailureSide.Card, $"it reached the most turns, {limits.MaxTurns} turns", usage);
            case "budget_exhausted":
                return Failed(FailureSide.Card, $"it reached its budget of {limits.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture)} USD", usage);
        }

        if (denials is { Count: > 0 } && access == AgentAccess.Write)
        {
            return Failed(FailureSide.Card, $"Claude Code was refused {denials.Count} action{(denials.Count == 1 ? "" : "s")}", usage);
        }

        if (exitCode != 0)
        {
            return Failed(FailureSide.OutsideCard, $"Claude Code exited with {exitCode}", usage);
        }

        if (output["is_error"] is not JsonValue isError || !isError.TryGetValue<bool>(out var error) || error)
        {
            return Failed(FailureSide.OutsideCard, "Claude Code reported an error", usage);
        }

        if (terminal != "completed")
        {
            return Failed(FailureSide.OutsideCard, $"Claude Code ended with {terminal ?? "no terminal_reason"}", usage);
        }

        if (denials is null)
        {
            return Failed(FailureSide.OutsideCard, "Claude Code's answer has no permission_denials", usage);
        }

        if (output["structured_output"] is not JsonObject report)
        {
            return Failed(FailureSide.Card, "it gave no report, or a report that is not an object", usage);
        }

        return AgentResult.Success((JsonObject)report.DeepClone(), usage, stderr, method, denied);
    }

    /// <summary>The usage as Claude Code gave it, those of its five fields it gave.</summary>
    private static JsonObject Usage(JsonObject output)
    {
        var usage = new JsonObject();
        foreach (var field in new[] { "usage", "modelUsage", "total_cost_usd", "duration_ms", "num_turns" })
        {
            if (output[field] is { } value)
            {
                usage[field] = value.DeepClone();
            }
        }

        return usage;
    }

    private static (bool LoggedIn, string? Method) ReadLogin(string stdout)
    {
        try
        {
            if (JsonNode.Parse(stdout) is JsonObject status)
            {
                var loggedIn = status["loggedIn"] is JsonValue value && value.TryGetValue<bool>(out var yes) && yes;
                return (loggedIn, Text(status, "authMethod"));
            }
        }
        catch (JsonException)
        {
        }

        return (false, null);
    }

    private static string? Text(JsonObject json, string field) =>
        json[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The outcome with the token replaced wherever it was written, before anything reads
    /// it.</summary>
    private static ProcessOutcome Scrubbed(ProcessOutcome outcome, string token) =>
        token.Length == 0
            ? outcome
            : outcome with
            {
                Stdout = outcome.Stdout.Replace(token, Redacted, StringComparison.Ordinal),
                Stderr = outcome.Stderr.Replace(token, Redacted, StringComparison.Ordinal),
            };
}