using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevFlow.Protocol;

/// <summary>The preset a setup run proposes for its project, one of the three the settings panel
/// offers.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SetupPreset>))]
public enum SetupPreset
{
    /// <summary>Questions off, every approved card merges by itself.</summary>
    [JsonStringEnumMemberName("fully_automatic")]
    FullyAutomatic,

    /// <summary>Questions off, a card waits for the owner sometimes, on the three conditions.</summary>
    [JsonStringEnumMemberName("guided")]
    Guided,

    /// <summary>Questions on, every card waits for the owner.</summary>
    [JsonStringEnumMemberName("only_i_merge")]
    OnlyIMerge,
}

/// <summary>What kind of thing a setup run could not settle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SetupUnsettledKind>))]
public enum SetupUnsettledKind
{
    /// <summary>The project can only be tested with a real secret, which a run is never given, so
    /// it cannot be connected until its tests need none.</summary>
    [JsonStringEnumMemberName("real_secret")]
    RealSecret,

    /// <summary>The project's tests start containers, which a worker cannot run yet.</summary>
    [JsonStringEnumMemberName("containers_in_tests")]
    ContainersInTests,

    /// <summary>The project needs macOS to build or test, which a worker in a Linux container is
    /// not.</summary>
    [JsonStringEnumMemberName("macos_needed")]
    MacosNeeded,

    /// <summary>Anything else it could not settle.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>
/// The report a setup run's agent hands back, the fixed form Dev flow reads, as <see cref="Report"/>
/// is the developer's: every field required, lists may be empty. Its JSON Schema,
/// contracts/devflow/setup.schema.json, is <see cref="Schema"/>, written from this type, and the
/// agent is given it; <see cref="TryRead"/> reads a report into this type with a missing, unknown,
/// null or mistyped field refused, and the form's own rules held. See "Setting up a project" in
/// devflow/README.md.
/// </summary>
/// <param name="Summary">What it wrote and why, in a few sentences.</param>
/// <param name="Proposed">The settings it proposes for the project.</param>
/// <param name="Variables">Each variable the project's tests or its start need a value for, which
/// the owner gives on the project's page; never a value.</param>
/// <param name="Unsettled">Each thing it could not settle, said plainly rather than guessed.</param>
/// <param name="Decisions">Each decision it made, as a build's report has them.</param>
/// <param name="Handover">A short note for whoever works on the setup next.</param>
public sealed partial record SetupReport(
    string Summary,
    SetupProposal Proposed,
    IReadOnlyList<SetupVariable> Variables,
    IReadOnlyList<SetupUnsettled> Unsettled,
    IReadOnlyList<ReportDecision> Decisions,
    string Handover)
{
    /// <summary>How a setup report is written and read, as a report is.</summary>
    public static JsonSerializerOptions Json => Form.Json;

    /// <summary>The setup report's JSON Schema, written from this type with <see cref="Json"/>:
    /// every object closed, every field required, and nothing nullable.</summary>
    public static JsonObject Schema() => Form.Schema<SetupReport>("Dev flow's setup report");

    /// <summary>
    /// Reads <paramref name="report"/> as a setup report. False, with <paramref name="error"/>
    /// saying what is wrong, when it is not an object; a field is missing, unknown, null or of
    /// another type; a list holds a null; the summary, the proposal's why, a path, a variable's why
    /// or something unsettled is blank; or a variable's name is not one a shell takes.
    /// </summary>
    public static bool TryRead(JsonElement report, [NotNullWhen(true)] out SetupReport? read, [NotNullWhen(false)] out string? error)
    {
        read = null;
        if (!Form.TryRead<SetupReport>(report, "setup report", out var parsed, out error))
        {
            return false;
        }

        error = Problem(parsed);
        if (error is not null)
        {
            return false;
        }

        read = parsed;
        return true;
    }

    /// <summary>What breaks the form's rules beyond the schema, or null when nothing does.</summary>
    private static string? Problem(SetupReport report)
    {
        if (report.Proposed.Paths.Contains(null))
        {
            return "proposed.paths: holds a null";
        }

        if (report.Variables.Contains(null))
        {
            return "variables: holds a null";
        }

        if (report.Unsettled.Contains(null))
        {
            return "unsettled: holds a null";
        }

        if (report.Decisions.Contains(null))
        {
            return "decisions: holds a null";
        }

        if (Blank(report.Summary))
        {
            return "summary: says what the setup wrote";
        }

        if (Blank(report.Proposed.Why))
        {
            return "proposed.why: says why the preset is proposed";
        }

        if (report.Proposed.Paths.Any(Blank))
        {
            return "proposed.paths: lists a path that is blank";
        }

        foreach (var variable in report.Variables)
        {
            if (!VariableName().IsMatch(variable.Name))
            {
                return Said($"variables: {variable.Name} is not a variable's name, letters, digits and underscores not beginning with a digit");
            }

            if (Blank(variable.Why))
            {
                return Said($"variables: {variable.Name} does not say why it is needed");
            }
        }

        return report.Unsettled.Any(unsettled => Blank(unsettled.Detail))
            ? "unsettled: says what it could not settle"
            : null;
    }

    /// <summary>A variable's name as a shell and Docker's <c>--env-file</c> take it.</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableName();

    private static bool Blank(string text) => text.Trim().Length == 0;

    private static string Said(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The settings a setup run proposes.</summary>
/// <param name="Preset">The preset it proposes.</param>
/// <param name="Why">Why it proposes it.</param>
/// <param name="Paths">The paths a card's pull request waits for the owner on, the paths of
/// <c>sometimes</c>, as the settings take them; empty when it proposes none.</param>
public sealed record SetupProposal(SetupPreset Preset, string Why, IReadOnlyList<string> Paths);

/// <summary>A variable the project's tests or its start need a value for.</summary>
/// <param name="Name">Its name, as the project reads it from its environment.</param>
/// <param name="Why">What needs it, and what kind of value it takes: a throwaway or a key made for
/// the automation, never production's.</param>
public sealed record SetupVariable(string Name, string Why);

/// <summary>Something a setup run could not settle.</summary>
/// <param name="Kind">What kind of thing it is.</param>
/// <param name="Detail">What it is, plainly.</param>
public sealed record SetupUnsettled(SetupUnsettledKind Kind, string Detail);