using System.Globalization;
using System.Text;

using DevFlow.Protocol;
using DevFlow.PullRequests;

namespace DevFlow.Setup;

/// <summary>
/// The body of a setup's pull request, which Dev flow opens as the app, titled
/// <see cref="SetupRunStore.Title"/>: what the agent wrote, what it proposes, the variables the tests
/// need, what it could not settle, its decisions and Dev flow's validation, and that it always waits
/// for the owner. Every text of the report is the agent's and data, made safe as a card's pull
/// request's is, by <see cref="PullRequestText.Text"/>. See "Setting up a project" in
/// devflow/README.md.
/// </summary>
public static class SetupText
{
    /// <summary>How each kind of what it could not settle is named.</summary>
    public static string Kind(SetupUnsettledKind kind) => kind switch
    {
        SetupUnsettledKind.RealSecret => "Needs a real secret",
        SetupUnsettledKind.ContainersInTests => "Its tests start containers",
        SetupUnsettledKind.MacosNeeded => "Needs macOS",
        _ => "Other",
    };

    /// <summary>How each preset is named on the settings panel.</summary>
    public static string Preset(SetupPreset preset) => preset switch
    {
        SetupPreset.FullyAutomatic => "Fully automatic",
        SetupPreset.Guided => "Guided",
        _ => "Only I merge",
    };

    /// <summary>The body for <paramref name="report"/>, from run <paramref name="runId"/> of setup
    /// <paramref name="number"/>, which pushed <paramref name="commit"/>; with why it stopped when
    /// <paramref name="stopped"/> is given.</summary>
    public static string Body(SetupReport report, int number, Guid runId, string commit, string? stopped = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(commit);
        var body = new StringBuilder();
        body.Append("## Summary\n\n").Append(PullRequestText.Text(report.Summary)).Append("\n\n");
        body.Append("This pull request writes `.devflow/Dockerfile`, the recipe every worker of this project is built from, and `.devflow/project.json`. It always waits for you to merge it, whatever the project's settings: the recipe decides what runs as root in your containers.\n\n");
        if (stopped is not null)
        {
            body.Append("**Stopped.** ").Append(PullRequestText.Text(stopped)).Append(". Comment here, and it runs again.\n\n");
        }

        body.Append("## What it could not settle\n\n");
        List(body, report.Unsettled.Select(each => $"**{Kind(each.Kind)}.** {PullRequestText.Text(each.Detail)}"));
        body.Append("## What it proposes\n\n")
            .Append(CultureInfo.InvariantCulture, $"**{Preset(report.Proposed.Preset)}.** {PullRequestText.Text(report.Proposed.Why)}\n\n");
        if (report.Proposed.Paths.Count > 0)
        {
            body.Append("The paths a card waits for you on:\n\n");
            List(body, report.Proposed.Paths.Select(path => PullRequestText.Code(path)));
        }

        body.Append("## The values its tests need\n\n");
        List(body, report.Variables.Select(variable => $"{PullRequestText.Code(variable.Name)}: {PullRequestText.Text(variable.Why)}"));
        body.Append("## Decisions the agent made\n\n");
        List(body, report.Decisions.Select(decision =>
            (decision.ForOwner ? "**For you.** " : "") + $"{PullRequestText.Text(decision.Chosen)}. Alternative: {PullRequestText.Text(decision.Alternative)}. Why: {PullRequestText.Text(decision.Why)}."));
        body.Append("## Verification\n\n")
            .Append(CultureInfo.InvariantCulture, $"Dev flow validated `.devflow/` on {commit[..7]}: the recipe copies no files and `project.json` follows its schema. The setup's worker cannot run the project's check; the first worker built from this recipe runs it before it takes any card.\n\n")
            .Append(CultureInfo.InvariantCulture, $"Set up by Dev flow, setup {number}, run {runId}.\n");
        return body.ToString();
    }

    private static void List(StringBuilder body, IEnumerable<string> items)
    {
        var any = false;
        foreach (var item in items)
        {
            body.Append("- ").Append(item).Append('\n');
            any = true;
        }

        body.Append(any ? "\n" : "None.\n\n");
    }
}