namespace DevFlow.Protocol;

/// <summary>
/// What a worker sends to <see cref="WorkerApi.LogRoute"/> while its job runs, from its start until
/// its result is sent: the lines its run wrote since the last post, none when there are none, and
/// its usage so far. Every post is the run's heartbeat, so a run whose worker sends nothing for
/// two minutes is lost. Each field is nullable so Dev flow answers one that is missing with
/// <c>422</c> naming it. See "Jobs" in devflow/README.md.
/// </summary>
/// <param name="WorkerId">The id it said hello with.</param>
/// <param name="Lines">The lines, at most <see cref="MostLines"/>, empty when there are none.</param>
/// <param name="Usage">The run's token figures so far, null when it has none to tell.</param>
public sealed record LogRequest(Guid? WorkerId, IReadOnlyList<LogLine?>? Lines, LogUsage? Usage = null)
{
    /// <summary>The most lines one post holds.</summary>
    public const int MostLines = 500;
}

/// <summary>A line of a run's log, as the worker wrote it.</summary>
/// <param name="Seq">Its place in the run's log, from 1, only growing; a line Dev flow already
/// holds is ignored, so a retried post never doubles one, and gaps are allowed.</param>
/// <param name="At">When the worker wrote it, by the worker's clock.</param>
/// <param name="Kind">One of <see cref="LogKinds.All"/>.</param>
/// <param name="Text">Its text, scrubbed by the worker of the token and the job's values, 1 to
/// <see cref="MostText"/> characters; a longer one is cut, ending in <see cref="CutMark"/>.</param>
public sealed record LogLine(long? Seq, DateTimeOffset? At, string? Kind, string? Text)
{
    /// <summary>The longest text a line keeps; a longer one is cut to it.</summary>
    public const int MostText = 4000;

    /// <summary>What ends a text that was cut.</summary>
    public const string CutMark = "… (cut)";
}

/// <summary>The kinds a <see cref="LogLine"/> may be.</summary>
public static class LogKinds
{
    /// <summary>What the agent wrote.</summary>
    public const string Text = "text";

    /// <summary>A tool call, as its name and a short summary of its input.</summary>
    public const string Tool = "tool";

    /// <summary>A tool's answer, cut.</summary>
    public const string ToolResult = "tool_result";

    /// <summary>A line of the check's output.</summary>
    public const string Check = "check";

    /// <summary>A line the worker, or Dev flow, writes itself.</summary>
    public const string System = "system";

    /// <summary>Every kind, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Text, Tool, ToolResult, Check, System];
}

/// <summary>A run's token figures so far, as Claude Code counts them, each a whole number, 0 or
/// more. Its cache reads are kept beside the rest and are never part of a token count.</summary>
public sealed record LogUsage(long? InputTokens, long? CacheCreationInputTokens, long? CacheReadInputTokens, long? OutputTokens);

/// <summary>What Dev flow answers a log post with.</summary>
/// <param name="Stop">Whether the owner marked the run to stop, so the worker ends it now.</param>
public sealed record LogAnswer(bool Stop);