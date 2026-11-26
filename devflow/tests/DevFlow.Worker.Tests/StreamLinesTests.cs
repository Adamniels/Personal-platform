using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

using Microsoft.Extensions.Time.Testing;

namespace DevFlow.Worker.Tests;

/// <summary>
/// Claude Code's stream as the run's log lines: which event makes which line, each scrubbed and
/// cut, and the usage so far. See "The agent interface" in devflow/README.md.
/// </summary>
public sealed class StreamLinesTests
{
    private const string Token = "sk-ant-oat01-TESTtokenVALUEthatMUSTneverLEAVEtheRUN_0123456789abcdef";

    [Fact]
    public void EachEventBecomesItsLine()
    {
        var (lines, log, clock) = Lines();
        var longCommand = "echo " + new string('a', 300);
        var longAnswer = new string('b', 2500);
        var todos = "{\"todos\":[{\"content\":\"" + new string('c', 300) + "\"}]}";
        var events = new JsonNode[]
        {
            Init(),
            Assistant("msg_1", new JsonObject { ["type"] = "text", ["text"] = "I'll look at the list first." }),
            Assistant("msg_1", Tool("Bash", new JsonObject { ["command"] = "npm test -- src/habits", ["description"] = "Run the tests" })),
            Assistant("msg_2", Tool("Read", new JsonObject { ["file_path"] = "/work/fodr/src/list.tsx", ["limit"] = 40 })),
            Assistant("msg_3", Tool("Edit", new JsonObject { ["file_path"] = "/work/fodr/src/list.tsx", ["old_string"] = "a", ["new_string"] = "b" })),
            Assistant("msg_4", Tool("Write", new JsonObject { ["file_path"] = "/work/fodr/NOTES.md", ["content"] = "notes" })),
            Assistant("msg_5", Tool("Glob", new JsonObject { ["pattern"] = "src/**/*.tsx" })),
            Assistant("msg_6", Tool("Bash", new JsonObject { ["command"] = longCommand })),
            Assistant("msg_7", Tool("TodoWrite", new JsonObject { ["todos"] = new JsonArray(new JsonObject { ["content"] = new string('c', 300) }) })),
            Answer("toolu_1", "3 passed"),
            Answer("toolu_2", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "line one" }, new JsonObject { ["type"] = "text", ["text"] = "line two" })),
            Answer("toolu_3", longAnswer),
            new JsonObject { ["type"] = "rate_limit_event", ["rate_limit_info"] = new JsonObject { ["status"] = "allowed", ["resetsAt"] = 1800032400 } },
            JsonNode.Parse(Recorded.Text("success.json"))!,
        };
        foreach (var @event in events)
        {
            lines.Read(@event.ToJsonString());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // A line that is not an event, and an empty one, which makes nothing.
        lines.Read("npm warn deprecated something");
        lines.Read("");

        Assert.Equal(
            [
                (RunLineKind.Text, "I'll look at the list first."),
                (RunLineKind.Tool, "Bash: npm test -- src/habits"),
                (RunLineKind.Tool, "Read: /work/fodr/src/list.tsx"),
                (RunLineKind.Tool, "Edit: /work/fodr/src/list.tsx"),
                (RunLineKind.Tool, "Write: /work/fodr/NOTES.md"),
                (RunLineKind.Tool, """Glob: {"pattern":"src/**/*.tsx"}"""),
                (RunLineKind.Tool, $"Bash: {longCommand[..199]}…"),
                (RunLineKind.Tool, $"TodoWrite: {todos[..199]}…"),
                (RunLineKind.ToolResult, "3 passed"),
                (RunLineKind.ToolResult, "line one\nline two"),
                (RunLineKind.ToolResult, new string('b', 2000) + "… (cut)"),
                (RunLineKind.System, "npm warn deprecated something"),
            ],
            log.Lines.Select(line => (line.Kind, line.Text)));

        // A summary is at most 200 characters.
        Assert.All(log.Lines.Where(line => line.Kind == RunLineKind.Tool), line => Assert.True(line.Text.Length - line.Text.IndexOf(": ", StringComparison.Ordinal) - 2 <= 200, line.Text));
        // Each line is at the worker's clock when its event was read.
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 8, 0, 1, TimeSpan.Zero), log.Lines[0].At);
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 8, 0, 14, TimeSpan.Zero), log.Lines[^1].At);
    }

    [Fact]
    public void TheRecordedSuccessMakesItsLines()
    {
        var (lines, log, _) = Lines();

        foreach (var line in Recorded.Stream("success").Split('\n'))
        {
            lines.Read(line);
        }

        Assert.Equal(
            [
                RunLineKind.Text, RunLineKind.Tool, RunLineKind.ToolResult,
                RunLineKind.Tool, RunLineKind.ToolResult,
                RunLineKind.Tool, RunLineKind.ToolResult,
                RunLineKind.Tool, RunLineKind.ToolResult,
            ],
            log.Lines.Select(line => line.Kind));
        Assert.Equal("Bash: npm test -- src/habits", log.Lines[5].Text);
    }

    [Fact]
    public void TheTokenAndValuesNeverReachALine()
    {
        // A value with a quote in it, which JSON would write escaped.
        const string Value = "pa\"ss-0123456789";
        var scrub = new ValueScrub([new JobValue("DATABASE_PASSWORD", Value)]);
        var clock = new FakeTimeProvider();
        var log = new MemoryRunLog(clock);
        var lines = new StreamLines(log, Token, scrub);
        var secrets = $"token {Token} and value {Value}";

        lines.Read(Assistant("msg_1", new JsonObject { ["type"] = "text", ["text"] = secrets }).ToJsonString());
        lines.Read(Assistant("msg_2", Tool("Bash", new JsonObject { ["command"] = $"curl -H 'Authorization: Bearer {Token}' -u u:{Value}" })).ToJsonString());
        lines.Read(Assistant("msg_3", Tool("WebFetch", new JsonObject { ["url"] = $"https://x.test/?k={Token}", ["prompt"] = Value })).ToJsonString());
        lines.Read(Answer("toolu_1", secrets).ToJsonString());
        lines.Read(Answer("toolu_2", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = secrets })).ToJsonString());
        lines.Read($"not an event: {secrets}");
        // The token straddling where a tool's answer is cut: scrubbed before the cut, so no part of
        // it is left.
        lines.Read(Answer("toolu_3", new string('x', 1990) + Token).ToJsonString());

        Assert.Equal(7, log.Lines.Count);
        foreach (var line in log.Lines)
        {
            Assert.DoesNotContain(Token[..16], line.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Value, line.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("pa\\\"ss", line.Text, StringComparison.Ordinal);
        }

        Assert.Contains("[redacted]", log.Lines[0].Text, StringComparison.Ordinal);
        Assert.Contains("[value of DATABASE_PASSWORD]", log.Lines[0].Text, StringComparison.Ordinal);
        Assert.Contains("[value of DATABASE_PASSWORD]", log.Lines[2].Text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", log.Lines[5].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageSoFarAddsUp()
    {
        var clock = new FakeTimeProvider();
        var log = new Usages(clock);
        var lines = new StreamLines(log, Token, ValueScrub.None);

        lines.Read(Assistant("msg_1", Tool("Read", new JsonObject { ["file_path"] = "a" }), Usage(5, 9102, 0, 240)).ToJsonString());
        lines.Read(Answer("toolu_1", "a").ToJsonString());
        lines.Read(Assistant("msg_2", Tool("Read", new JsonObject { ["file_path"] = "b" }), Usage(12, 4551, 170210, 2100)).ToJsonString());
        lines.Read(Assistant("msg_3", Tool("Read", new JsonObject { ["file_path"] = "c" }), Usage(12, 4551, 170330, 1673)).ToJsonString());

        Assert.Equal(
            [
                new RunUsage(5, 9102, 0, 240),
                new RunUsage(17, 13653, 170210, 2340),
                new RunUsage(29, 18204, 340540, 4013),
            ],
            log.Told);
        Assert.Equal(new RunUsage(29, 18204, 340540, 4013), lines.UsageSoFar);
        Assert.Equal(lines.UsageSoFar, log.Usage);
    }

    [Fact]
    public void AMessageSplitIntoEventsCountsOnce()
    {
        // The recorded success sends its first message as two events, each with the message's
        // whole usage: counted once, the usage so far ends at the result's own.
        var (lines, _, _) = Lines();

        foreach (var line in Recorded.Stream("success").Split('\n'))
        {
            lines.Read(line);
        }

        var result = JsonNode.Parse(Recorded.Text("success.json"))!["usage"]!;
        Assert.Equal(
            new RunUsage((long)result["input_tokens"]!, (long)result["cache_creation_input_tokens"]!, (long)result["cache_read_input_tokens"]!, (long)result["output_tokens"]!),
            lines.UsageSoFar);
    }

    [Theory]
    [InlineData("""{"type": "assistant"}""")]
    [InlineData("""{"type": "assistant", "message": {"content": "not a list", "usage": "none"}}""")]
    [InlineData("""{"type": "assistant", "message": {"content": [{"type": "tool_use"}, 3, null]}}""")]
    [InlineData("""{"type": "user", "message": {"content": [{"type": "tool_result", "content": 42}]}}""")]
    [InlineData("""{"type": 7}""")]
    [InlineData("[1, 2]")]
    public void AShapeItDoesNotKnowNeverThrows(string line)
    {
        var (lines, log, _) = Lines();

        lines.Read(line);

        Assert.All(log.Lines, entry => Assert.NotNull(entry.Text));
    }

    private static (StreamLines Lines, MemoryRunLog Log, FakeTimeProvider Clock) Lines()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2027, 1, 15, 8, 0, 0, TimeSpan.Zero));
        var log = new MemoryRunLog(clock);
        return (new StreamLines(log, Token, ValueScrub.None), log, clock);
    }

    private static JsonObject Init() => new() { ["type"] = "system", ["subtype"] = "init", ["cwd"] = "/work/fodr", ["claude_code_version"] = "2.1.325" };

    private static JsonObject Assistant(string id, JsonObject block, JsonObject? usage = null) => new()
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject
        {
            ["id"] = id,
            ["type"] = "message",
            ["role"] = "assistant",
            ["content"] = new JsonArray(block),
            ["usage"] = usage ?? Usage(1, 0, 0, 1),
        },
        ["parent_tool_use_id"] = null,
    };

    private static JsonObject Tool(string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = $"toolu_{name}", ["name"] = name, ["input"] = input };

    private static JsonObject Answer(string id, JsonNode content) => new()
    {
        ["type"] = "user",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["tool_use_id"] = id, ["type"] = "tool_result", ["content"] = content }),
        },
    };

    private static JsonObject Usage(long input, long creation, long read, long output) => new()
    {
        ["input_tokens"] = input,
        ["cache_creation_input_tokens"] = creation,
        ["cache_read_input_tokens"] = read,
        ["output_tokens"] = output,
        ["service_tier"] = "standard",
    };

    /// <summary>A log that keeps every usage it was told, in order.</summary>
    private sealed class Usages(TimeProvider time) : IRunLog
    {
        private readonly MemoryRunLog _log = new(time);

        public List<RunUsage> Told { get; } = [];

        public RunUsage Usage => _log.Usage;

        public void Add(RunLineKind kind, string text) => _log.Add(kind, text);

        public void Used(RunUsage soFar)
        {
            Told.Add(soFar);
            _log.Used(soFar);
        }
    }
}