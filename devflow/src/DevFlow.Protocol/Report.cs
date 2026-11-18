using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DevFlow.Protocol;

/// <summary>Whether the agent built the card, or stopped.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportOutcome>))]
public enum ReportOutcome
{
    [JsonStringEnumMemberName("done")]
    Done,

    [JsonStringEnumMemberName("stopped")]
    Stopped,
}

/// <summary>
/// The report a developer agent hands back, the fixed form Dev flow reads. Every field is
/// required, and lists may be empty. Its JSON Schema, contracts/devflow/report.schema.json, is
/// <see cref="Schema"/>, written from this type, and the agent is given it; .NET can write a JSON
/// Schema but not validate against one, so <see cref="TryRead"/> reads a report into this type with
/// a missing, unknown or mistyped field refused. The report guides Dev flow and decides nothing. See
/// "Handing out work" in devflow/docs/devflow-architecture.md.
/// </summary>
/// <param name="Outcome">Done, or stopped when the card could not be built.</param>
/// <param name="StoppedReason">Why it stopped; empty when it is done.</param>
/// <param name="Summary">What was built, in a few sentences.</param>
/// <param name="Tasks">Each task of the card, and whether it is done.</param>
/// <param name="Decisions">Each decision the agent made.</param>
/// <param name="Tests">Each test written or run, and its result.</param>
/// <param name="Pages">The pages the change affects, and whether the agent looked at each.</param>
/// <param name="Installed">Each tool the agent installed.</param>
/// <param name="FollowUps">Work it suggests that is not this card's.</param>
/// <param name="Replies">Its answer to each finding of a review; empty on a build.</param>
/// <param name="Handover">A short note for whoever works on the card next.</param>
public sealed record Report(
    ReportOutcome Outcome,
    string StoppedReason,
    string Summary,
    IReadOnlyList<ReportTask> Tasks,
    IReadOnlyList<ReportDecision> Decisions,
    IReadOnlyList<ReportTest> Tests,
    IReadOnlyList<ReportPage> Pages,
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> FollowUps,
    IReadOnlyList<ReportReply> Replies,
    string Handover)
{
    /// <summary>How a report is written and read: snake case, every constructor parameter
    /// required, no field the type does not have, no null where the type has none.</summary>
    public static JsonSerializerOptions Json => Form.Json;

    /// <summary>The report's JSON Schema, written from this type with <see cref="Json"/>: every
    /// object closed, every field required, and nothing nullable.</summary>
    public static JsonObject Schema() => Form.Schema<Report>("Dev flow's developer report");

    /// <summary>
    /// Reads <paramref name="report"/> as a report. False, with <paramref name="error"/> saying
    /// what is wrong, when it is not an object, a field is missing, unknown, null or of another type,
    /// <c>outcome</c> is neither word, or <c>stopped_reason</c> is empty on a stop or written on a
    /// done.
    /// </summary>
    public static bool TryRead(JsonElement report, [NotNullWhen(true)] out Report? read, [NotNullWhen(false)] out string? error)
    {
        read = null;
        if (!Form.TryRead<Report>(report, "report", out var parsed, out error))
        {
            return false;
        }

        if (NullIn(parsed) is { } field)
        {
            error = $"{field}: holds a null";
            return false;
        }

        switch (parsed.Outcome)
        {
            case ReportOutcome.Stopped when parsed.StoppedReason.Trim().Length == 0:
                error = "stopped_reason: must say why when the outcome is stopped";
                return false;
            case ReportOutcome.Done when parsed.StoppedReason.Length > 0:
                error = "stopped_reason: must be empty when the outcome is done";
                return false;
        }

        read = parsed;
        error = null;
        return true;
    }

    /// <summary>The first list that holds a null, which nullable annotations do not cover inside a
    /// list; null when none does.</summary>
    private static string? NullIn(Report report) =>
        report.Tasks.Contains(null) ? "tasks"
        : report.Decisions.Contains(null) ? "decisions"
        : report.Tests.Contains(null) ? "tests"
        : report.Pages.Contains(null) ? "pages"
        : report.Installed.Contains(null) ? "installed"
        : report.FollowUps.Contains(null) ? "follow_ups"
        : report.Replies.Contains(null) ? "replies"
        : null;
}

/// <summary>One task of the card.</summary>
public sealed record ReportTask(string Text, bool Done);

/// <summary>A decision the agent made where the card left it open.</summary>
/// <param name="Chosen">What it chose.</param>
/// <param name="Alternative">What else it could have done.</param>
/// <param name="Why">Why it chose so.</param>
/// <param name="ForOwner">Whether the owner would want a say.</param>
public sealed record ReportDecision(string Chosen, string Alternative, string Why, bool ForOwner);

/// <summary>A test the agent wrote or ran.</summary>
public sealed record ReportTest(string What, string Result);

/// <summary>A page the change affects.</summary>
/// <param name="Path">Its address in the project, such as <c>/habits</c>.</param>
/// <param name="Why">How the change affects it.</param>
/// <param name="Looked">Whether the agent looked at it itself.</param>
public sealed record ReportPage(string Path, string Why, bool Looked);

/// <summary>The agent's answer to a review's finding.</summary>
public sealed record ReportReply(string Finding, string Answer);