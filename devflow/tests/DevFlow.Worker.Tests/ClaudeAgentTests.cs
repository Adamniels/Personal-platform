using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Worker.Agents;

using Microsoft.Extensions.Time.Testing;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests;

/// <summary>
/// Claude Code behind the agent interface, against the streams of its runs recorded in
/// <c>Claude/stream/</c>: what counts as a success, the environment the run gets, the login checked
/// first, the reset of a usage limit, and the token never leaving the run's environment. See "The
/// agent interface" in devflow/README.md.
/// </summary>
public sealed class ClaudeAgentTests
{
    /// <summary>Shaped like a subscription token, and found nowhere it must not be.</summary>
    private const string Token = "sk-ant-oat01-TESTtokenVALUEthatMUSTneverLEAVEtheRUN_0123456789abcdef";

    private const string Schema = """{"type": "object", "required": ["summary"], "properties": {"summary": {"type": "string"}}}""";

    /// <summary>The morning the usage limit's cases are read on.</summary>
    private static readonly DateTimeOffset Morning = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("success", 0, null, null)]
    // subtype reads success on a failure, and is never used to decide.
    [InlineData("success-with-is-error", 0, "OutsideCard", "reported an error")]
    [InlineData("not-logged-in", 1, "OutsideCard", "API answered an error")]
    [InlineData("invalid-api-key", 1, "OutsideCard", "401")]
    [InlineData("usage-limit", 1, "OutsideCard", "429")]
    // Nothing at all, as root with bypassPermissions, and a crash before any event: no result.
    [InlineData("root-bypass.txt", 1, "OutsideCard", "Claude Code ended without a result (exit 1)")]
    [InlineData("not-json.txt", 1, "OutsideCard", "Claude Code ended without a result (exit 1)")]
    [InlineData("max-turns", 1, "Card", "200 turns")]
    [InlineData("budget", 1, "Card", "25 USD")]
    [InlineData("refused-write", 0, "Card", "refused 1 action")]
    [InlineData("no-report", 0, "Card", "no report")]
    [InlineData("report-not-an-object", 0, "Card", "no report")]
    // A clean answer with the wrong exit code is not a success either.
    [InlineData("success", 1, "OutsideCard", "exited with 1")]
    public async Task ASuccessIsOnlyWhatTheRuleSays(string @case, int exitCode, string? side, string? reason)
    {
        var stdout = @case.EndsWith(".txt", StringComparison.Ordinal) ? Recorded.Text(@case) : Recorded.Stream(@case);
        var (result, _, log) = await Run(new ProcessOutcome(exitCode, stdout, "something on stderr", false));

        if (side is null)
        {
            Assert.True(result.Succeeded);
            Assert.Null(result.Failure);
            Assert.NotNull(result.Report);
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Null(result.Report);
            Assert.Equal(Enum.Parse<FailureSide>(side), result.Failure!.Side);
            Assert.Contains(reason!, result.Failure.Reason, StringComparison.Ordinal);
        }

        // stderr is kept and the login recorded either way.
        Assert.Equal("something on stderr", result.Stderr);
        Assert.Equal("oauth_token", result.AuthMethod);
        NoToken(result, log);
    }

    [Fact]
    public async Task TheRunStreams()
    {
        foreach (var access in new[] { AgentAccess.Write, AgentAccess.ReadOnly })
        {
            var (_, starter, _) = await Run(access: access);

            var arguments = starter.Started[1].Spec.Arguments.ToList();
            var format = arguments.IndexOf("--output-format");
            Assert.Equal("stream-json", arguments[format + 1]);
            Assert.Equal("--verbose", arguments[format + 2]);
            Assert.Single(arguments, argument => argument == "--output-format");
            Assert.Single(arguments, argument => argument == "--verbose");
            Assert.DoesNotContain("json", arguments);
        }
    }

    /// <summary>Every case in <c>Claude/stream/</c>, with the exit code it ends with.</summary>
    public static TheoryData<string, int> StreamCases { get; } = new()
    {
        { "success", 0 },
        { "success-with-is-error", 0 },
        { "not-logged-in", 1 },
        { "invalid-api-key", 1 },
        { "usage-limit", 1 },
        { "usage-limit-resets-today", 1 },
        { "usage-limit-resets-date", 1 },
        { "max-turns", 1 },
        { "budget", 1 },
        { "refused-write", 0 },
        { "no-report", 0 },
        { "report-not-an-object", 0 },
    };

    [Fact]
    public void EveryStreamIsACaseWithItsTwin()
    {
        var streams = Directory.GetFiles(Recorded.File("stream"), "*.jsonl").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
        Assert.Equal(StreamCases.Select(row => row.Data.Item1).Order(StringComparer.Ordinal), streams);
        Assert.All(streams, @case => Assert.True(File.Exists(Recorded.File($"{@case}.json")), @case));
    }

    [Theory]
    [MemberData(nameof(StreamCases))]
    public async Task EveryRecordedCaseJudgesAsBefore(string @case, int exitCode)
    {
        foreach (var access in new[] { AgentAccess.Write, AgentAccess.ReadOnly })
        {
            var (streamed, _, log) = await Run(Recorded.Run(@case, exitCode, "on stderr"), access: access, time: new FakeTimeProvider(Morning));
            var twin = ClaudeCodeAgent.JudgeAnswer(
                JsonNode.Parse(Recorded.Text($"{@case}.json"))!.AsObject(),
                exitCode,
                "on stderr",
                Limits,
                "oauth_token",
                access,
                Morning);

            Assert.Equal(twin.Succeeded, streamed.Succeeded);
            Assert.Equal(twin.Failure, streamed.Failure);
            Assert.True(JsonNode.DeepEquals(twin.Report, streamed.Report), $"{@case}: {streamed.Report}");
            Assert.True(JsonNode.DeepEquals(twin.Usage, streamed.Usage), $"{@case}: {streamed.Usage}");
            Assert.True(JsonNode.DeepEquals(twin.Denials, streamed.Denials), $"{@case}: {streamed.Denials}");
            Assert.Equal(twin.Stderr, streamed.Stderr);
            NoToken(streamed, log);
        }
    }

    [Fact]
    public async Task TheResetIsReadFromTheRateLimitEvent()
    {
        var at17 = DateTimeOffset.FromUnixTimeSeconds(1800032400);
        var at1930 = DateTimeOffset.FromUnixTimeSeconds(1800041400);
        static string Event(string status, long? resetsAt) =>
            new JsonObject
            {
                ["type"] = "rate_limit_event",
                ["rate_limit_info"] = new JsonObject { ["status"] = status, ["resetsAt"] = resetsAt, ["rateLimitType"] = "five_hour" },
                ["session_id"] = "6a0f1c62-4b1e-4f3c-9d2a-0b7c1e5d9a15",
            }.ToJsonString();

        // The result's text says 3pm; the event says 17:00, and wins. The last such event counts.
        var (fromEvent, _, _) = await Reset("usage-limit-resets-today", Event("rejected", 1800000000), Event("rejected", 1800032400));
        Assert.Equal(FailureSide.OutsideCard, fromEvent.Failure!.Side);
        Assert.Equal(at17, fromEvent.Failure.ResetsAt);
        Assert.Contains("resets at 2027-01-15 17:00 UTC", fromEvent.Failure.Reason, StringComparison.Ordinal);

        // A result whose text says no time this reads takes the event's.
        Assert.Equal(at1930, (await Reset("usage-limit", Event("rejected", 1800041400))).Result.Failure!.ResetsAt);

        // One whose resetsAt is not set, or none at all, leaves the text to say it, as before.
        var today = DateTimeOffset.Parse("2027-01-15T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(today, (await Reset("usage-limit-resets-today", Event("rejected", null))).Result.Failure!.ResetsAt);
        Assert.Equal(today, (await Reset("usage-limit-resets-today")).Result.Failure!.ResetsAt);
        Assert.Null((await Reset("usage-limit")).Result.Failure!.ResetsAt);

        // An event that let the request through says when the window resets, not that the run was
        // stopped by it, so it is not a reset to wait for.
        Assert.Equal(today, (await Reset("usage-limit-resets-today", Event("allowed", 1800032400), Event("allowed_warning", 1800032400))).Result.Failure!.ResetsAt);

        // A run that did not end on the API's error has no reset, whatever came before.
        Assert.True((await Reset("success", Event("rejected", 1800032400))).Result.Succeeded);
    }

    [Fact]
    public async Task AStreamWithNoResultFailsOutside()
    {
        var stream = Recorded.Stream("success");
        var withoutResult = string.Join('\n', stream.TrimEnd('\n').Split('\n')[..^1]) + "\n";

        foreach (var exitCode in new[] { 0, 143 })
        {
            var (result, _, log) = await Run(new ProcessOutcome(exitCode, withoutResult, "", false));

            Assert.Equal(FailureSide.OutsideCard, result.Failure!.Side);
            Assert.Equal($"Claude Code ended without a result (exit {exitCode})", result.Failure.Reason);
            Assert.Empty(result.Usage);
            NoToken(result, log);
        }
    }

    [Fact]
    public async Task TheRunsLinesReachItsLog()
    {
        var log = new MemoryRunLog(new WatchedClock());
        var stream = Recorded.Stream("success", result => result["result"] = $"done, {Token}");
        var lines = stream.TrimEnd('\n').Split('\n').ToList();
        lines.Insert(2, $"a stray line with {Token}");

        var (result, starter, worker) = await Run(new ProcessOutcome(0, string.Join('\n', lines) + "\n", "on stderr", false), runLog: log);

        Assert.True(result.Succeeded, result.Failure?.Reason);
        // The run's stream, as it was read: standard error makes no line, and the login check none.
        Assert.Null(starter.Started[0].Spec.Lines);
        Assert.Equal(
            [RunLineKind.Text, RunLineKind.System, RunLineKind.Tool, RunLineKind.ToolResult, RunLineKind.Tool, RunLineKind.ToolResult, RunLineKind.Tool, RunLineKind.ToolResult, RunLineKind.Tool, RunLineKind.ToolResult],
            log.Lines.Select(line => line.Kind));
        Assert.Equal("a stray line with [redacted]", log.Lines[1].Text);
        Assert.Equal(new RunUsage(41, 18204, 512870, 6113), log.Usage);
        NoToken(result, worker, log);
    }

    [Fact]
    public async Task LinesThatAreNotJsonDoNotChangeTheAnswer()
    {
        var lines = Recorded.Stream("success").TrimEnd('\n').Split('\n').ToList();
        lines.Insert(1, "npm warn something unrelated");
        lines.Insert(3, "{ half an event");
        lines.Insert(5, "");

        var (result, _, _) = await Run(new ProcessOutcome(0, string.Join('\n', lines) + "\n", "", false));

        Assert.True(result.Succeeded, result.Failure?.Reason);
        Assert.Equal("Added the empty state to the habits list.", (string)result.Report!["summary"]!);
    }

    [Fact]
    public async Task AReadOnlyRunIsNotFailedForWhatItWasRefused()
    {
        // A reviewer that tried to write was refused, which is how it is built: the run succeeds,
        // and what it was refused is kept.
        var (reviewed, reviewer, log) = await Run(Recorded.Run("refused-write", 0), access: AgentAccess.ReadOnly);

        Assert.True(reviewed.Succeeded, reviewed.Failure?.Reason);
        Assert.Equal("Could not write the file.", (string)reviewed.Report!["summary"]!);
        Assert.Equal("Write", (string)Assert.Single(reviewed.Denials!)!["tool_name"]!);
        var arguments = reviewer.Started[1].Spec.Arguments.ToList();
        Assert.Equal("default", arguments[arguments.IndexOf("--permission-mode") + 1]);
        Assert.Equal("none", arguments[arguments.IndexOf("--permission-prompts") + 1]);
        Assert.Equal("Read,Grep,Glob", arguments[arguments.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("bypassPermissions", arguments);
        NoToken(reviewed, log);

        // A developer refused the same is a failure in the card, and keeps them too.
        var (built, developer, _) = await Run(Recorded.Run("refused-write", 0));

        Assert.False(built.Succeeded);
        Assert.Single(built.Denials!);
        var developing = developer.Started[1].Spec.Arguments.ToList();
        Assert.Equal("bypassPermissions", developing[developing.IndexOf("--permission-mode") + 1]);
        Assert.DoesNotContain("--allowedTools", developing);
        Assert.DoesNotContain("--permission-prompts", developing);
    }

    [Fact]
    public async Task ASuccessAnswersTheReportAndTheUsageAsTheyAre()
    {
        var (result, _, log) = await Run(Recorded.Run("success", 0));

        Assert.True(result.Succeeded);
        Assert.Equal("Added the empty state to the habits list.", (string)result.Report!["summary"]!);
        // The result event's usage, not the usage so far.
        Assert.Equal(["usage", "modelUsage", "total_cost_usd", "duration_ms", "num_turns"], result.Usage.Select(pair => pair.Key));
        Assert.Equal(1.8342m, (decimal)result.Usage["total_cost_usd"]!);
        Assert.Equal(512870, (int)result.Usage["usage"]!["cache_read_input_tokens"]!);
        Assert.Equal(6113, (int)result.Usage["modelUsage"]!["claude-opus-5-5"]!["outputTokens"]!);
        Assert.Equal(184213, (int)result.Usage["duration_ms"]!);
        Assert.Equal(23, (int)result.Usage["num_turns"]!);
        NoToken(result, log);
    }

    [Fact]
    public async Task ARunPastTheTimeLimitIsTheCards()
    {
        var (result, starter, log) = await Run(new ProcessOutcome(-1, "", "", TimedOut: true));

        Assert.Equal(FailureSide.Card, result.Failure!.Side);
        Assert.Contains("90 minutes", result.Failure.Reason, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(90), starter.Started[1].TimeLimit);
        NoToken(result, log);
    }

    [Fact]
    public async Task TheEnvironmentIsBuiltFromNothing()
    {
        var (result, starter, log) = await Run(Recorded.Run("success", 0));

        Assert.True(result.Succeeded);
        Assert.Equal(2, starter.Started.Count);
        foreach (var (spec, _) in starter.Started)
        {
            Assert.Equal("claude", spec.FileName);
            Assert.Equal("/work/fodr", spec.WorkingDirectory);
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["HOME"] = "/home/agent",
                    ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
                    ["LANG"] = "C.UTF-8",
                    ["CLAUDE_CONFIG_DIR"] = "/home/agent/.claude",
                    ["CLAUDE_CODE_OAUTH_TOKEN"] = Token,
                    // The image's Chromium, so the pictures the brief asks the agent to take of
                    // its own pages find a browser.
                    ["PLAYWRIGHT_BROWSERS_PATH"] = "/ms-playwright",
                }.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                spec.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            Assert.DoesNotContain("ANTHROPIC_API_KEY", spec.Environment.Keys);
            Assert.DoesNotContain("CLAUDE_CODE_SIMPLE", spec.Environment.Keys);
            Assert.DoesNotContain("--bare", spec.Arguments);
            Assert.DoesNotContain(spec.Arguments, argument => argument.Contains(Token, StringComparison.Ordinal));
        }

        Assert.Equal(["auth", "status", "--json"], starter.Started[0].Spec.Arguments);
        Assert.Equal(
            [
                "-p", "Add an empty state to the habits list.",
                "--model", "opus",
                "--permission-mode", "bypassPermissions",
                "--output-format", "stream-json", "--verbose",
                "--json-schema", Schema,
                "--max-turns", "200",
                "--max-budget-usd", "25",
            ],
            starter.Started[1].Spec.Arguments);
        NoToken(result, log);
    }

    [Fact]
    public async Task TheJobsModelIsPassed()
    {
        // Each run is started with the model and the limits its job carried, whatever the last run
        // was given.
        foreach (var (model, access) in new[] { ("sonnet", AgentAccess.Write), ("haiku", AgentAccess.ReadOnly), ("opus", AgentAccess.Write) })
        {
            var (result, starter, _) = await Run(access: access, model: model, limits: new AgentLimits(50, 25m, TimeSpan.FromMinutes(30)));

            Assert.True(result.Succeeded, result.Failure?.Reason);
            var arguments = starter.Started[1].Spec.Arguments.ToList();
            Assert.Equal(model, arguments[arguments.IndexOf("--model") + 1]);
            Assert.Single(arguments, argument => argument == "--model");
            Assert.Equal("50", arguments[arguments.IndexOf("--max-turns") + 1]);
            Assert.Equal(TimeSpan.FromMinutes(30), starter.Started[1].TimeLimit);
        }

        // The worker has no model of its own any more: the job is the one place it comes from.
        Assert.Null(typeof(ClaudeCodeOptions).GetProperty("Model"));
        Assert.Null(typeof(AgentLimits).GetProperty("Model"));
    }

    [Theory]
    [InlineData("auth-not-logged-in.json", 1, "none")]
    [InlineData("auth-api-key.json", 0, "api_key")]
    [InlineData("not-json.txt", 0, null)]
    [InlineData("root-bypass.txt", 1, null)]
    // Logged in with the token, but the command itself failed.
    [InlineData("auth-oauth-token.json", 1, "oauth_token")]
    public async Task TheLoginIsCheckedFirst(string file, int exitCode, string? method)
    {
        var (result, starter, log) = await Run(login: new ProcessOutcome(exitCode, Recorded.Text(file), "auth stderr", false));

        // The run never starts.
        Assert.Single(starter.Started);
        Assert.Equal(["auth", "status", "--json"], starter.Started[0].Spec.Arguments);
        Assert.False(result.Succeeded);
        Assert.Equal(FailureSide.OutsideCard, result.Failure!.Side);
        Assert.Contains("not logged in with the token", result.Failure.Reason, StringComparison.Ordinal);
        // The method is recorded, whatever it was.
        Assert.Equal(method, result.AuthMethod);
        Assert.Equal("auth stderr", result.Stderr);
        NoToken(result, log);
    }

    [Fact]
    public async Task TheTokenIsNeverWrittenAnywhere()
    {
        // Claude Code, or the agent inside it, writing the token into everything it answers.
        var stream = Recorded.Stream("success", result =>
        {
            result["structured_output"]!["summary"] = $"the token is {Token}";
            result["result"] = Token;
        });
        var (result, _, log) = await Run(
            new ProcessOutcome(0, stream, $"debug: CLAUDE_CODE_OAUTH_TOKEN={Token}\n", false),
            login: new ProcessOutcome(0, $$"""{"loggedIn": true, "authMethod": "oauth_token", "token": "{{Token}}"}""", Token, false));

        Assert.True(result.Succeeded);
        Assert.Contains("[redacted]", (string)result.Report!["summary"]!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", result.Stderr, StringComparison.Ordinal);
        NoToken(result, log);

        // Nor through the credential printed by mistake.
        var credential = new AgentCredential(Token);
        Assert.DoesNotContain(Token, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, new AgentRun("/work", "brief", Schema, "opus", Limits, credential).ToString(), StringComparison.Ordinal);

        // And a failure keeps none either.
        var (failed, _, failedLog) = await Run(new ProcessOutcome(1, $"crashed with {Token}", Token, false));
        Assert.False(failed.Succeeded);
        NoToken(failed, failedLog);
    }

    /// <summary>The token's value in nothing the run answers or logs: the report, the reason, the
    /// usage, the stderr kept and the method, as the result is sent back, and the worker's log.</summary>
    private static void NoToken(AgentResult result, StringBuilder log, MemoryRunLog? runLog = null)
    {
        Assert.All(runLog?.Lines ?? [], line => Assert.DoesNotContain(Token, line.Text, StringComparison.Ordinal));
        var sent = JsonSerializer.Serialize(new
        {
            report = result.Report,
            failure = result.Failure,
            usage = result.Usage,
            stderr = result.Stderr,
            method = result.AuthMethod,
        });
        Assert.DoesNotContain(Token, sent, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A usage limit's run of <paramref name="case"/> with <paramref name="events"/> just
    /// before its result, read on <see cref="Morning"/>.</summary>
    private static Task<(AgentResult Result, FakeStarter Starter, StringBuilder Log)> Reset(string @case, params string[] events)
    {
        var lines = Recorded.Stream(@case).TrimEnd('\n').Split('\n').ToList();
        lines.InsertRange(lines.Count - 1, events);
        return Run(new ProcessOutcome(@case == "success" ? 0 : 1, string.Join('\n', lines) + "\n", "", false), time: new FakeTimeProvider(Morning));
    }

    /// <summary>Runs the agent on a brief in <c>/work/fodr</c>, with <paramref name="login"/>
    /// answering the login check, logged in with the token unless a test says otherwise, and
    /// <paramref name="run"/> the run.</summary>
    private static async Task<(AgentResult Result, FakeStarter Starter, StringBuilder Log)> Run(ProcessOutcome? run = null, ProcessOutcome? login = null, AgentAccess access = AgentAccess.Write, string model = "opus", AgentLimits? limits = null, TimeProvider? time = null, IRunLog? runLog = null)
    {
        var starter = new FakeStarter(login ?? new ProcessOutcome(0, Recorded.Text("auth-oauth-token.json"), "", false), run ?? Recorded.Run("success", 0));
        var output = Writer(out var log);
        var agent = new ClaudeCodeAgent(starter, ClaudeCodeOptions.Default, new WorkerLog(output, new WatchedClock()), time);
        var result = await agent.Run(
            new AgentRun("/work/fodr", "Add an empty state to the habits list.", Schema, model, limits ?? Limits, new AgentCredential(Token), access, Log: runLog),
            Ct);
        return (result, starter, log);
    }
}