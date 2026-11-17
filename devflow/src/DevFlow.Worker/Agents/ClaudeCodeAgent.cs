using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevFlow.Worker.Agents;

/// <summary>Where Claude Code is, the user's home it runs in, and the model; the rest of its
/// environment is built from these and nothing else.</summary>
internal sealed record ClaudeCodeOptions(string Executable, string Home, string Path, string Lang, string Model)
{
    public static ClaudeCodeOptions Default { get; } = new("claude", "/home/agent", "/usr/local/bin:/usr/bin:/bin", "C.UTF-8", "claude-opus-5-5");
}

/// <summary>
/// Claude Code run headless, as the experiment of 2026-11-16 found it must be. See "The agent
/// interface" in devflow/README.md, and "Workers" in devflow/docs/devflow-architecture.md for why.
/// <list type="bullet">
/// <item>The environment is built from nothing: <c>HOME</c>, <c>PATH</c>, <c>LANG</c>,
/// <c>CLAUDE_CONFIG_DIR</c> in that home, and <c>CLAUDE_CODE_OAUTH_TOKEN</c>. An
/// <c>ANTHROPIC_API_KEY</c> would silently win over the token and bill the API, so none is ever
/// passed; nor is <c>--bare</c>, which refuses the token.</item>
/// <item>The login is checked first, with <c>claude auth status --json</c>, which must answer
/// <c>loggedIn</c> true and <c>authMethod</c> <c>oauth_token</c>; the method is recorded.</item>
/// <item>A run succeeds only when every condition of <see cref="Judge"/> holds; <c>subtype</c> is
/// never read, since it says <c>success</c> on some failures.</item>
/// <item>The token's value is replaced in everything read back, so neither the report, stderr nor
/// a reason can carry it out.</item>
/// </list>
/// </summary>
internal sealed class ClaudeCodeAgent(IProcessStarter starter, ClaudeCodeOptions options, WorkerLog log) : IAgent
{
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
        var environment = Environment(token);

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
        var answer = Scrubbed(await starter.Run(
            new ProcessSpec(options.Executable, Arguments(run), run.WorkingDirectory, environment),
            run.Limits.Time,
            cancellationToken), token);
        var result = Judge(answer, run.Limits, method);
        log.Write(result.Failure is { } failure
            ? $"The run failed, {(failure.Side == FailureSide.Card ? "in the card" : "outside the card")}: {failure.Reason}."
            : "The run succeeded with a report.");
        return result;
    }

    private Dictionary<string, string> Environment(string token) => new()
    {
        ["HOME"] = options.Home,
        ["PATH"] = options.Path,
        ["LANG"] = options.Lang,
        ["CLAUDE_CONFIG_DIR"] = $"{options.Home.TrimEnd('/')}/.claude",
        [TokenVariable] = token,
    };

    private List<string> Arguments(AgentRun run) =>
    [
        "-p", run.Brief,
        "--model", options.Model,
        "--permission-mode", "bypassPermissions",
        "--output-format", "json",
        "--json-schema", run.ReportSchema,
        "--max-turns", run.Limits.MaxTurns.ToString(CultureInfo.InvariantCulture),
        "--max-budget-usd", run.Limits.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// The rule, in order, the first that applies deciding. A success needs all of: exit 0;
    /// stdout valid JSON; <c>is_error</c> false; <c>terminal_reason</c> <c>completed</c>;
    /// <c>structured_output</c> present and an object; <c>permission_denials</c> empty. Outside the
    /// card: no answer, an answer that is not JSON, an <c>api_error</c> of any kind (not logged in,
    /// a refused key, a usage limit), and anything else Claude Code reports as an error. In the card:
    /// the time limit, <c>max_turns</c>, <c>budget_exhausted</c>, refused actions and no valid
    /// report.
    /// </summary>
    internal static AgentResult Judge(ProcessOutcome answer, AgentLimits limits, string? method)
    {
        AgentResult Failed(FailureSide side, string reason, JsonObject? usage = null) =>
            AgentResult.Failed(side, reason, usage ?? [], answer.Stderr, method);

        if (answer.TimedOut)
        {
            return Failed(FailureSide.Card, $"it ran past the time limit of {limits.Time.TotalMinutes:0} minutes and was ended");
        }

        if (string.IsNullOrWhiteSpace(answer.Stdout))
        {
            return Failed(FailureSide.OutsideCard, $"Claude Code gave no answer (exit {answer.ExitCode})");
        }

        JsonObject? output;
        try
        {
            output = JsonNode.Parse(answer.Stdout) as JsonObject;
        }
        catch (JsonException)
        {
            output = null;
        }

        if (output is null)
        {
            return Failed(FailureSide.OutsideCard, $"Claude Code's answer is not JSON (exit {answer.ExitCode})");
        }

        var usage = Usage(output);
        var terminal = Text(output, "terminal_reason");
        var denials = output["permission_denials"] as JsonArray;
        switch (terminal)
        {
            case "api_error":
                var status = output["api_error_status"] is JsonValue code && code.TryGetValue<int>(out var number) ? $" {number}" : "";
                return Failed(FailureSide.OutsideCard, $"Claude's API answered an error{status}", usage);
            case "max_turns":
                return Failed(FailureSide.Card, $"it reached the most turns, {limits.MaxTurns} turns", usage);
            case "budget_exhausted":
                return Failed(FailureSide.Card, $"it reached its budget of {limits.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture)} USD", usage);
        }

        if (denials is { Count: > 0 })
        {
            return Failed(FailureSide.Card, $"Claude Code was refused {denials.Count} action{(denials.Count == 1 ? "" : "s")}", usage);
        }

        if (answer.ExitCode != 0)
        {
            return Failed(FailureSide.OutsideCard, $"Claude Code exited with {answer.ExitCode}", usage);
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

        return AgentResult.Success((JsonObject)report.DeepClone(), usage, answer.Stderr, method);
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