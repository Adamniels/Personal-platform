using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Worker.Agents;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests;

/// <summary>
/// Claude Code behind the agent interface, against its answers recorded in <c>Claude/</c>: what
/// counts as a success, the environment the run gets, the login checked first, and the token never
/// leaving the run's environment. See "The agent interface" in devflow/README.md.
/// </summary>
public sealed class ClaudeAgentTests
{
    /// <summary>Shaped like a subscription token, and found nowhere it must not be.</summary>
    private const string Token = "sk-ant-oat01-TESTtokenVALUEthatMUSTneverLEAVEtheRUN_0123456789abcdef";

    private const string Schema = """{"type": "object", "required": ["summary"], "properties": {"summary": {"type": "string"}}}""";

    [Theory]
    [InlineData("success.json", 0, null, null)]
    // subtype reads success on a failure, and is never used to decide.
    [InlineData("success-with-is-error.json", 0, "OutsideCard", "reported an error")]
    [InlineData("not-logged-in.json", 1, "OutsideCard", "API answered an error")]
    [InlineData("invalid-api-key.json", 1, "OutsideCard", "401")]
    [InlineData("usage-limit.json", 1, "OutsideCard", "429")]
    [InlineData("root-bypass.txt", 1, "OutsideCard", "no answer")]
    [InlineData("not-json.txt", 1, "OutsideCard", "not JSON")]
    [InlineData("max-turns.json", 1, "Card", "200 turns")]
    [InlineData("budget.json", 1, "Card", "25 USD")]
    [InlineData("refused-write.json", 0, "Card", "refused 1 action")]
    [InlineData("no-report.json", 0, "Card", "no report")]
    [InlineData("report-not-an-object.json", 0, "Card", "no report")]
    // A clean answer with the wrong exit code is not a success either.
    [InlineData("success.json", 1, "OutsideCard", "exited with 1")]
    public async Task ASuccessIsOnlyWhatTheRuleSays(string file, int exitCode, string? side, string? reason)
    {
        var (result, _, log) = await Run(Answer(file, exitCode, stderr: "something on stderr"));

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
    public async Task ASuccessAnswersTheReportAndTheUsageAsTheyAre()
    {
        var (result, _, log) = await Run(Answer("success.json", 0));

        Assert.True(result.Succeeded);
        Assert.Equal("Added the empty state to the habits list.", (string)result.Report!["summary"]!);
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
        var (result, starter, log) = await Run(Answer("success.json", 0));

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
                "--model", "claude-opus-5-5",
                "--permission-mode", "bypassPermissions",
                "--output-format", "json",
                "--json-schema", Schema,
                "--max-turns", "200",
                "--max-budget-usd", "25",
            ],
            starter.Started[1].Spec.Arguments);
        NoToken(result, log);
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
        var (result, starter, log) = await Run(login: Answer(file, exitCode, stderr: "auth stderr"));

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
        var report = JsonNode.Parse(File.ReadAllText(Recorded("success.json")))!;
        report["structured_output"]!["summary"] = $"the token is {Token}";
        report["result"] = Token;
        var (result, _, log) = await Run(
            new ProcessOutcome(0, report.ToJsonString(), $"debug: CLAUDE_CODE_OAUTH_TOKEN={Token}\n", false),
            login: new ProcessOutcome(0, $$"""{"loggedIn": true, "authMethod": "oauth_token", "token": "{{Token}}"}""", Token, false));

        Assert.True(result.Succeeded);
        Assert.Contains("[redacted]", (string)result.Report!["summary"]!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", result.Stderr, StringComparison.Ordinal);
        NoToken(result, log);

        // Nor through the credential printed by mistake.
        var credential = new AgentCredential(Token);
        Assert.DoesNotContain(Token, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, new AgentRun("/work", "brief", Schema, AgentLimits.Default, credential).ToString(), StringComparison.Ordinal);

        // And a failure keeps none either.
        var (failed, _, failedLog) = await Run(new ProcessOutcome(1, $"crashed with {Token}", Token, false));
        Assert.False(failed.Succeeded);
        NoToken(failed, failedLog);
    }

    /// <summary>The token's value in nothing the run answers or logs: the report, the reason, the
    /// usage, the stderr kept and the method, as the result is sent back, and the worker's log.</summary>
    private static void NoToken(AgentResult result, StringBuilder log)
    {
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

    private static string Recorded(string file) => Path.Combine(AppContext.BaseDirectory, "Claude", file);

    /// <summary>A recorded answer as the process would end with it.</summary>
    private static ProcessOutcome Answer(string file, int exitCode, string stderr = "") =>
        new(exitCode, File.ReadAllText(Recorded(file)), stderr, TimedOut: false);

    /// <summary>Runs the agent on a brief in <c>/work/fodr</c>, with <paramref name="login"/>
    /// answering the login check, logged in with the token unless a test says otherwise, and
    /// <paramref name="run"/> the run.</summary>
    private static async Task<(AgentResult Result, FakeStarter Starter, StringBuilder Log)> Run(ProcessOutcome? run = null, ProcessOutcome? login = null)
    {
        var starter = new FakeStarter(login ?? Answer("auth-oauth-token.json", 0), run ?? Answer("success.json", 0));
        var output = Writer(out var log);
        var agent = new ClaudeCodeAgent(starter, ClaudeCodeOptions.Default, new WorkerLog(output, new WatchedClock()));
        var result = await agent.Run(
            new AgentRun("/work/fodr", "Add an empty state to the habits list.", Schema, AgentLimits.Default, new AgentCredential(Token)),
            Ct);
        return (result, starter, log);
    }

    /// <summary>Answers each start with the next outcome, and records what it was asked to
    /// start.</summary>
    private sealed class FakeStarter(params ProcessOutcome[] outcomes) : IProcessStarter
    {
        private readonly Queue<ProcessOutcome> _outcomes = new(outcomes);

        public List<(ProcessSpec Spec, TimeSpan TimeLimit)> Started { get; } = [];

        public Task<ProcessOutcome> Run(ProcessSpec spec, TimeSpan timeLimit, CancellationToken cancellationToken)
        {
            Started.Add((spec, timeLimit));
            return Task.FromResult(_outcomes.Dequeue());
        }
    }
}