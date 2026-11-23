using System.Text.Json.Nodes;

namespace DevFlow.Worker.Agents;

/// <summary>
/// The one narrow interface an agent runs behind: given a working copy, the brief, the report's
/// schema, the limits and the credential, it answers a result. Two implementations:
/// <see cref="FakeAgent"/> for tests, which spends no usage, and <see cref="ClaudeCodeAgent"/> for
/// real runs. See "The agent interface" in devflow/README.md.
/// </summary>
internal interface IAgent
{
    Task<AgentResult> Run(AgentRun run, CancellationToken cancellationToken);
}

/// <summary>One run of an agent.</summary>
/// <param name="WorkingDirectory">The clean working copy it works in.</param>
/// <param name="Brief">What it is asked to do, as text.</param>
/// <param name="ReportSchema">The JSON Schema its report must follow, as JSON text.</param>
/// <param name="Model">The model it runs on, as its job carried it.</param>
/// <param name="Limits">When it is stopped.</param>
/// <param name="Credential">What it logs in with.</param>
/// <param name="Access">Whether it may change files and run commands, as a build's developer
/// does, or only read, as a review's reviewer does.</param>
/// <param name="Values">The project's values its job carried, for the run's environment and
/// nowhere else; null or empty when it carried none.</param>
internal sealed record AgentRun(string WorkingDirectory, string Brief, string ReportSchema, string Model, AgentLimits Limits, AgentCredential Credential, AgentAccess Access = AgentAccess.Write, IReadOnlyList<DevFlow.Protocol.JobValue>? Values = null);

/// <summary>What an agent may do in its run.</summary>
internal enum AgentAccess
{
    /// <summary>Change files and run commands in its working copy: a build's developer.</summary>
    Write,

    /// <summary>Read, search and list files, and nothing else: a review's reviewer.</summary>
    ReadOnly,
}

/// <summary>When a run is stopped, as its job carried it.</summary>
/// <param name="MaxTurns">The most turns, which Claude Code enforces.</param>
/// <param name="MaxBudgetUsd">The most it may cost, which Claude Code enforces, overshooting by
/// about a turn.</param>
/// <param name="Time">The longest it may take, which the worker enforces by ending the
/// process.</param>
internal sealed record AgentLimits(int MaxTurns, decimal MaxBudgetUsd, TimeSpan Time);

/// <summary>
/// The secret an agent logs in with, Claude Code's subscription token. It is handed to the run in
/// the environment only, never written to a file, logged or sent back, so it says nothing of its
/// value when printed, and is read only by <see cref="Reveal"/>.
/// </summary>
internal sealed class AgentCredential(string token)
{
    /// <summary>The token itself, for the one place that hands it to the run.</summary>
    public string Reveal() => token;

    public override string ToString() => "AgentCredential(redacted)";
}

/// <summary>Where a failure lies: in the card, which the card's own work caused, or outside it,
/// in the login, the API or the machine, which another try may not repeat.</summary>
internal enum FailureSide
{
    Card,
    OutsideCard,
}

/// <summary>Why a run failed, and where the failure lies.</summary>
/// <param name="TimedOut">Whether it reached its time limit and was ended.</param>
/// <param name="ResetsAt">When Claude's usage limit resets, when its answer said so; null
/// otherwise.</param>
internal sealed record AgentFailure(FailureSide Side, string Reason, bool TimedOut = false, DateTimeOffset? ResetsAt = null);

/// <summary>
/// What a run answers: the report on success, or the failure, and either way the usage as the
/// agent reported it, the standard error kept, and how it was logged in.
/// </summary>
/// <param name="Report">The report, an object; null on a failure.</param>
/// <param name="Failure">Why it failed; null on a success.</param>
/// <param name="Usage">The agent's own figures, as they are; empty when it gave none.</param>
/// <param name="Stderr">Its standard error.</param>
/// <param name="AuthMethod">How Claude Code said it was logged in; null for an agent without a
/// login or when it said nothing.</param>
/// <param name="Denials">The actions it was refused, as Claude Code listed them; empty when none
/// were, or the agent says nothing of them.</param>
internal sealed record AgentResult(JsonObject? Report, AgentFailure? Failure, JsonObject Usage, string Stderr, string? AuthMethod, JsonArray? Denials = null)
{
    public bool Succeeded => Failure is null;

    public static AgentResult Success(JsonObject report, JsonObject usage, string stderr, string? authMethod, JsonArray? denials = null) =>
        new(report, null, usage, stderr, authMethod, denials);

    public static AgentResult Failed(FailureSide side, string reason, JsonObject usage, string stderr, string? authMethod, JsonArray? denials = null) =>
        new(null, new AgentFailure(side, reason), usage, stderr, authMethod, denials);

    /// <summary>A run ended at its time limit, a failure in the card.</summary>
    public static AgentResult TimedOut(AgentLimits limits, JsonObject usage, string stderr, string? authMethod, JsonArray? denials = null) =>
        new(null, new AgentFailure(FailureSide.Card, $"it ran past the time limit of {limits.Time.TotalMinutes:0} minutes and was ended", TimedOut: true), usage, stderr, authMethod, denials);
}