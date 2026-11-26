using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;

namespace DevFlow.Worker.Agents;

/// <summary>
/// Turns each line of Claude Code's stream, <c>--output-format stream-json --verbose</c>, into the
/// log line it makes, as it is read, and keeps the run's usage so far. See "The agent interface" in
/// devflow/README.md.
/// <list type="bullet">
/// <item>An <c>assistant</c> event's <c>text</c> block is a <see cref="RunLineKind.Text"/> line; its
/// <c>tool_use</c> block a <see cref="RunLineKind.Tool"/> line, the tool's name and a summary of its
/// input of at most <see cref="MostSummary"/> characters: <c>Bash</c>'s command, the path of
/// <c>Read</c>, <c>Edit</c> and <c>Write</c>, and any other tool's input as JSON.</item>
/// <item>A <c>user</c> event's <c>tool_result</c> block is a <see cref="RunLineKind.ToolResult"/>
/// line, cut to <see cref="MostResult"/> characters with <see cref="CutMark"/> at the end.</item>
/// <item>A line that is not a JSON object is a <see cref="RunLineKind.System"/> line, cut the same
/// way; every other event, <c>system</c> <c>init</c>, <c>result</c> and <c>rate_limit_event</c>
/// among them, makes none.</item>
/// <item>Every line has the token replaced by <see cref="ClaudeCodeAgent.Redacted"/> and the job's
/// values by <see cref="ValueScrub"/> before it is cut, so a cut never leaves part of one, and
/// before it reaches the log.</item>
/// <item>The usage so far is the sum of each assistant message's <c>usage</c>, told to the log after
/// each assistant event. Claude Code sends a message of several blocks as one event per block, each
/// with the message's <c>id</c> and its whole <c>usage</c>, so a message counts once, as its last
/// event says.</item>
/// </list>
/// It never throws on what the stream holds: a shape it does not know makes no line.
/// </summary>
internal sealed class StreamLines(IRunLog log, string token, ValueScrub values)
{
    /// <summary>The longest summary of a tool's input.</summary>
    public const int MostSummary = 200;

    /// <summary>How much of a tool's answer, or of a line that is not an event, a line keeps.</summary>
    public const int MostResult = 2000;

    /// <summary>What ends a tool's answer that was cut.</summary>
    public const string CutMark = "… (cut)";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Lock _lock = new();
    private readonly Dictionary<string, RunUsage> _messages = new(StringComparer.Ordinal);
    private RunUsage _withoutId = RunUsage.Zero;

    /// <summary>The run's token figures so far.</summary>
    public RunUsage UsageSoFar
    {
        get
        {
            lock (_lock)
            {
                return Sum();
            }
        }
    }

    /// <summary>Reads one line of the stream, as Claude Code wrote it.</summary>
    public void Read(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (ClaudeCodeAgent.Event(line) is not { } @event)
        {
            log.Add(RunLineKind.System, Cut(Clean(line), MostResult, CutMark));
            return;
        }

        var type = @event["type"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (type == "assistant" && @event["message"] is JsonObject message)
        {
            Assistant(message);
        }
        else if (type == "user" && @event["message"] is JsonObject answer && answer["content"] is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>().Where(block => String(block, "type") == "tool_result"))
            {
                log.Add(RunLineKind.ToolResult, Cut(Clean(ResultText(block["content"])), MostResult, CutMark));
            }
        }
    }

    private void Assistant(JsonObject message)
    {
        if (message["content"] is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                switch (String(block, "type"))
                {
                    case "text" when String(block, "text") is { Length: > 0 } text:
                        log.Add(RunLineKind.Text, Clean(text));
                        break;
                    case "tool_use":
                        var name = String(block, "name") ?? "a tool";
                        var summary = Summary(name, block["input"]);
                        log.Add(RunLineKind.Tool, summary.Length == 0 ? Clean(name) : $"{Clean(name)}: {summary}");
                        break;
                }
            }
        }

        if (message["usage"] is not JsonObject usage)
        {
            return;
        }

        var figures = new RunUsage(Number(usage, "input_tokens"), Number(usage, "cache_creation_input_tokens"), Number(usage, "cache_read_input_tokens"), Number(usage, "output_tokens"));
        RunUsage soFar;
        lock (_lock)
        {
            if (String(message, "id") is { Length: > 0 } id)
            {
                _messages[id] = figures;
            }
            else
            {
                _withoutId += figures;
            }

            soFar = Sum();
        }

        log.Used(soFar);
    }

    /// <summary>What a tool's input says in a line: the command, the path, or the input as JSON,
    /// scrubbed and cut to <see cref="MostSummary"/>.</summary>
    private string Summary(string tool, JsonNode? input)
    {
        var field = tool switch
        {
            "Bash" => "command",
            "Read" or "Edit" or "Write" => "file_path",
            _ => null,
        };
        string text;
        if (field is not null && input is JsonObject named && String(named, field) is { } said)
        {
            text = Clean(said);
        }
        else if (input is null)
        {
            text = "";
        }
        else
        {
            // The values are scrubbed from the input's strings before it is written as JSON, whose
            // escaping would otherwise hide a value holding a quote or a backslash.
            text = Clean(values.Scrub(input)!.ToJsonString(Compact));
        }

        return Cut(text, MostSummary, "…");
    }

    /// <summary>A tool's answer as text: a string as it is, or its <c>text</c> blocks joined by
    /// lines.</summary>
    private static string ResultText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Join('\n', blocks.OfType<JsonObject>().Where(block => String(block, "type") == "text").Select(block => String(block, "text") ?? "")),
        _ => "",
    };

    /// <summary><paramref name="text"/> with the token and the job's values replaced.</summary>
    private string Clean(string text) =>
        values.Scrub(token.Length == 0 ? text : text.Replace(token, ClaudeCodeAgent.Redacted, StringComparison.Ordinal));

    /// <summary><paramref name="text"/> when it is at most <paramref name="most"/> characters, and
    /// otherwise its start with <paramref name="mark"/>: for a summary, the mark within the most,
    /// and for a tool's answer, after the most it keeps. A pair of UTF-16 halves is never split.</summary>
    private static string Cut(string text, int most, string mark)
    {
        if (text.Length <= most)
        {
            return text;
        }

        var keep = mark == CutMark ? most : most - mark.Length;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return new StringBuilder(text, 0, keep, keep + mark.Length).Append(mark).ToString();
    }

    private RunUsage Sum() => _messages.Values.Aggregate(_withoutId, (sum, usage) => sum + usage);

    private static string? String(JsonObject json, string field) =>
        json[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long Number(JsonObject json, string field) =>
        json[field] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}