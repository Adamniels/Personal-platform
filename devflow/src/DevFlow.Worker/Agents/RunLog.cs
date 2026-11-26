namespace DevFlow.Worker.Agents;

/// <summary>
/// What a log line of a run is, one of the five kinds the feature "Dev flow: running now, usage and
/// history" names. <see cref="Text"/>, <see cref="Tool"/> and <see cref="ToolResult"/> are read from
/// the agent's stream by <see cref="StreamLines"/>; <see cref="Check"/> and <see cref="System"/> are
/// the worker's own, and <see cref="StreamLines"/> writes a line of the stream that is not an event
/// as <see cref="System"/>. See "The agent interface" in devflow/README.md.
/// </summary>
internal enum RunLineKind
{
    /// <summary>What the agent wrote.</summary>
    Text,

    /// <summary>A tool call, as its name and a short summary of its input.</summary>
    Tool,

    /// <summary>A tool's answer, cut.</summary>
    ToolResult,

    /// <summary>A line of the check's output.</summary>
    Check,

    /// <summary>A line the worker writes itself.</summary>
    System,
}

/// <summary>A run's token figures, as Claude Code counts them in a message's <c>usage</c>.</summary>
internal sealed record RunUsage(long InputTokens, long CacheCreationInputTokens, long CacheReadInputTokens, long OutputTokens)
{
    public static RunUsage Zero { get; } = new(0, 0, 0, 0);

    public static RunUsage operator +(RunUsage left, RunUsage right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new(
            left.InputTokens + right.InputTokens,
            left.CacheCreationInputTokens + right.CacheCreationInputTokens,
            left.CacheReadInputTokens + right.CacheReadInputTokens,
            left.OutputTokens + right.OutputTokens);
    }
}

/// <summary>
/// Where a run's log lines go as the agent works, each already scrubbed of the token and the job's
/// values, and the run's usage so far. An agent writes to it from the thread that reads its output.
/// </summary>
internal interface IRunLog
{
    /// <summary>Adds a line, at the worker's clock now.</summary>
    void Add(RunLineKind kind, string text);

    /// <summary>The run's token figures so far, each time they change.</summary>
    void Used(RunUsage soFar);
}