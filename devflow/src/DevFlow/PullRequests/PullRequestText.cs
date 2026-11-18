using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using DevFlow.Protocol;

namespace DevFlow.PullRequests;

/// <summary>What a built card's pull request says, from its report and its run.</summary>
/// <param name="Card">The card's issue number, which the pull request closes.</param>
/// <param name="RunId">The run that built it, which the last line names.</param>
/// <param name="Report">The run's report, read into its form.</param>
/// <param name="Check">The check Dev flow ran, from the project's <c>.devflow/project.json</c>;
/// null for a run handed out before runs kept it.</param>
/// <param name="Commit">The commit the check ran on, the branch's head.</param>
/// <param name="ExitCode">How the check ended.</param>
public sealed record PullRequestSource(int Card, Guid RunId, Report Report, string? Check, string Commit, int ExitCode);

/// <summary>
/// A built card's pull request body, in the layout this repository's agents write theirs, as
/// <c>.claude/skills/pick-up-slice/templates.md</c> lays it out, so the owner reads Dev flow's pull
/// requests as he reads the runner's: Summary with the one <c>Closes</c> line, Tasks, Decisions the
/// agent made, Verification, Follow ups, and a last line that leads back to the card and the run.
/// See "The pull request Dev flow opens" in devflow/README.md.
/// </summary>
public static partial class PullRequestText
{
    /// <summary>The body, its sections in their order, each a level two heading with a blank line
    /// around it, and a newline at its end.</summary>
    public static string Body(PullRequestSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var report = source.Report;
        var body = new StringBuilder();
        Section(body, "Summary");
        body.Append(Text(report.Summary)).Append("\n\n");
        body.Append(CultureInfo.InvariantCulture, $"Closes #{source.Card}\n\n");

        Section(body, "Tasks");
        List(body, report.Tasks.Select(task => $"[{(task.Done ? 'x' : ' ')}] {Text(task.Text)}"));

        Section(body, "Decisions the agent made");
        List(body, report.Decisions.Select(Decision));

        Section(body, "Verification");
        body.Append(CultureInfo.InvariantCulture, $"Dev flow ran {Code(source.Check ?? "the project's check")} on {source.Commit[..Math.Min(7, source.Commit.Length)]}: exit {source.ExitCode}.\n\n");
        foreach (var test in report.Tests)
        {
            body.Append(CultureInfo.InvariantCulture, $"- {Text(test.What)}: {Text(test.Result)}\n");
        }

        if (report.Tests.Count > 0)
        {
            body.Append('\n');
        }

        Section(body, "Follow ups");
        List(body, report.FollowUps.Select(Text));

        body.Append(CultureInfo.InvariantCulture, $"Built by Dev flow for #{source.Card}, run {source.RunId}.\n");
        return body.ToString();
    }

    /// <summary>A decision as one line: what was chosen, the alternative and why, each a sentence
    /// and each left out when the agent wrote nothing for it; one marked for the owner begins
    /// <c>**For you.**</c>.</summary>
    private static string Decision(ReportDecision decision)
    {
        var parts = new List<string>();
        if (decision.ForOwner)
        {
            parts.Add("**For you.**");
        }

        AddSentence(parts, "", decision.Chosen);
        AddSentence(parts, "Alternative: ", decision.Alternative);
        AddSentence(parts, "Why: ", decision.Why);
        return string.Join(' ', parts);
    }

    private static void AddSentence(List<string> parts, string label, string text)
    {
        var said = Text(text);
        if (said.Length == 0)
        {
            return;
        }

        parts.Add(label + (said[^1] is '.' or '!' or '?' ? said : said + "."));
    }

    private static void Section(StringBuilder body, string heading) =>
        body.Append("## ").Append(heading).Append("\n\n");

    /// <summary>Each item as a list item, or <c>None.</c> when there is none.</summary>
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

    /// <summary>
    /// Text from the report, the agent's and so data, made safe to place inside one of the body's
    /// sections: one line, so it cannot begin a block of its own, a heading, a fence, a quote or a
    /// table; <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> written as entities, so no HTML, a comment
    /// that would hide the sections after it among them, is inserted; its backslashes and backticks
    /// escaped, so no backslash of the agent's can undo an escape and the only code spans are the
    /// ones made here; a <c>#</c> it begins with escaped, so a list item cannot hold a heading; a
    /// closing keyword followed by an issue, <c>Closes #12</c>, <c>fixes owner/name#3</c> or an
    /// issue's address, with the keyword in a code span, so it closes nothing; and an <c>@</c>
    /// mention in a code span, so the app notifies nobody. What it says is kept, to be read.
    /// </summary>
    internal static string Text(string text)
    {
        var line = new StringBuilder(text.Length);
        foreach (var c in OneLine(text))
        {
            line.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '\\' => "\\\\",
                '`' => "\\`",
                _ => c.ToString(),
            });
        }

        var safe = line.ToString();
        if (safe.StartsWith('#'))
        {
            safe = "\\" + safe;
        }

        safe = ClosingKeyword().Replace(safe, match => $"`{match.Value}`");
        return Mention().Replace(safe, match => $"`{match.Value}`");
    }

    /// <summary>A keyword GitHub closes an issue with, when what follows it, after an optional
    /// colon, is an issue: <c>#12</c>, <c>GH-12</c>, <c>owner/name#12</c> or an address. Emphasis
    /// around the keyword or the issue, <c>**Closes** #12</c> or <c>fixes _#12_</c>, does not hide
    /// one, since whether GitHub reads through it is not something to rely on.</summary>
    [GeneratedRegex(@"(?<![a-z0-9])(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)(?![a-z0-9])(?=[*_]*:?[*_]*\s+[*_]*(?:#\d|GH-\d|[\w.-]+/[\w.-]+#\d|https?://))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClosingKeyword();

    /// <summary>An <c>@</c> mention of a login or a team, as GitHub notifies one: not inside a word,
    /// such as an address.</summary>
    [GeneratedRegex(@"(?<!\w)@[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})(?:/[A-Za-z0-9_.-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Mention();

    /// <summary>Every kind of line break made a space, and the ends trimmed.</summary>
    private static string OneLine(string text)
    {
        var line = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            line.Append(c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029' or '\v' or '\f' ? ' ' : c);
        }

        return line.ToString().Trim();
    }

    /// <summary>A code span of <paramref name="text"/> made one line, fenced with one backtick more
    /// than its longest run of them, and padded when it begins or ends with one, as CommonMark
    /// reads a code span.</summary>
    internal static string Code(string text)
    {
        var line = OneLine(text);
        var longest = 0;
        var run = 0;
        foreach (var c in line)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', longest + 1);
        var pad = line.StartsWith('`') || line.EndsWith('`') ? " " : "";
        return $"{fence}{pad}{line}{pad}{fence}";
    }
}