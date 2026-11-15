using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DevFlow.Runner;

/// <summary>A decision read from the body, and whether it is marked for the owner.</summary>
internal sealed record BodyDecision(OwnerDecision Text, bool IsOwners);

/// <summary>
/// Reads a pull request body as <c>.claude/skills/pick-up-slice/templates.md</c> lays it out, and
/// the reviewer's verdict as <c>.claude/agents/reviewer.md</c> does. Headings are matched exactly,
/// at the start of a line and outside a fenced block; a section runs to the next heading of its
/// level or above. Text is kept as written: lines are joined with a space and trimmed, and nothing
/// else is changed but the owner's mark taken out of a decision.
/// </summary>
internal static partial class PullRequestBody
{
    private const string SummaryHeading = "## Summary";
    private const string DecisionsHeading = "## Decisions the agent made";
    private const string ForTheOwnerHeading = "## For the owner";

    /// <summary>How long the summary may be, in characters, the cut included.</summary>
    private const int SummaryLength = 300;

    /// <summary>The first paragraph under <c>## Summary</c> that is not the <c>Closes</c> line, as
    /// one line of at most 300 characters, or empty.</summary>
    internal static string Summary(string? body)
    {
        foreach (var paragraph in Paragraphs(Section(body, SummaryHeading)))
        {
            var text = string.Join(' ', paragraph.Where(line => !ClosesLine().IsMatch(line)));
            if (text.Length > 0)
            {
                return Cut(text);
            }
        }

        return "";
    }

    /// <summary>The number in the summary's <c>Closes #&lt;n&gt;</c> line, or null.</summary>
    internal static int? Slice(string? body)
    {
        foreach (var line in Section(body, SummaryHeading) ?? [])
        {
            var match = ClosesLine().Match(line.Trim());
            if (match.Success && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return null;
    }

    /// <summary>
    /// The entries under <c>## Decisions the agent made</c>, each split at the first
    /// <c> Alternative: </c> or <c> Alternatives: </c> and the first <c> Why: </c> after it, or kept
    /// whole as what was chosen. A decision is the owner's when its text has the mark, a bold span
    /// starting <c>**owner-decision</c>, which is taken out. A missing section, or one that says
    /// <c>None.</c>, has none.
    /// </summary>
    internal static IReadOnlyList<BodyDecision> Decisions(string? body)
    {
        var entries = Entries(Section(body, DecisionsHeading));
        return [.. entries.Select(entry =>
        {
            var owners = OwnerMark().IsMatch(entry);
            return new BodyDecision(Split(owners ? WithoutMark(entry) : entry), owners);
        })];
    }

    /// <summary>The entries under <c>## For the owner</c> in a verdict, or none when it says
    /// <c>None.</c> or is missing.</summary>
    internal static IReadOnlyList<string> ReviewerView(string? verdictBody) => Entries(Section(verdictBody, ForTheOwnerHeading));

    /// <summary>
    /// The lines of the first section with the heading, up to the next heading of its level or
    /// above, or null when there is none. Line endings are made <c>\n</c> first.
    /// </summary>
    private static List<string>? Section(string? text, string heading)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var level = heading.IndexOf(' ', StringComparison.Ordinal);
        List<string>? section = null;
        var fence = new Fence();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var inFence = fence.Read(line);
            if (!inFence && section is null && line == heading)
            {
                section = [];
                continue;
            }

            if (section is null)
            {
                continue;
            }

            if (!inFence && HeadingLevel(line) is > 0 and var lineLevel && lineLevel <= level)
            {
                break;
            }

            section.Add(line);
        }

        return section;
    }

    /// <summary>How many <c>#</c> start the line, when they are followed by a space; 0 when it is
    /// not a heading.</summary>
    private static int HeadingLevel(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == '#')
        {
            count++;
        }

        return count > 0 && count < line.Length && line[count] == ' ' ? count : 0;
    }

    /// <summary>The section's paragraphs, each its trimmed lines, split at blank lines.</summary>
    private static IEnumerable<List<string>> Paragraphs(List<string>? lines)
    {
        var paragraph = new List<string>();
        foreach (var line in lines ?? [])
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (paragraph.Count > 0)
                {
                    yield return paragraph;
                    paragraph = [];
                }

                continue;
            }

            paragraph.Add(line.Trim());
        }

        if (paragraph.Count > 0)
        {
            yield return paragraph;
        }
    }

    /// <summary>
    /// The section's entries, or none when its first text says <c>None.</c>. An entry is a top
    /// level bullet, <c>-</c>, <c>*</c> or <c>+</c> at the start of a line, with every line after
    /// it that is not another one: indented lines and nested bullets, blank lines between them,
    /// and an unindented line straight after it. An unindented paragraph after a blank line is
    /// an entry of its own, so no text in the section is dropped. Lines are trimmed and joined
    /// with a space.
    /// </summary>
    private static List<string> Entries(List<string>? lines)
    {
        var entries = new List<string>();
        if (lines is null || lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) is not { } first
            || first.TrimStart().StartsWith("None.", StringComparison.Ordinal))
        {
            return entries;
        }

        StringBuilder? entry = null;
        var afterBlank = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                afterBlank = true;
                continue;
            }

            var bullet = line.Length >= 2 && line[0] is '-' or '*' or '+' && line[1] == ' ';
            var indented = char.IsWhiteSpace(line[0]);
            if (bullet || entry is null || (afterBlank && !indented))
            {
                Flush();
                entry = new StringBuilder(bullet ? line[2..].Trim() : line.Trim());
            }
            else
            {
                entry.Append(' ').Append(line.Trim());
            }

            afterBlank = false;
        }

        Flush();
        return entries;

        void Flush()
        {
            if (entry is { Length: > 0 })
            {
                entries.Add(entry.ToString());
            }

            entry = null;
        }
    }

    /// <summary>A decision split into what was chosen, the alternative and the reason, or kept
    /// whole as what was chosen when it has no alternative.</summary>
    private static OwnerDecision Split(string text)
    {
        var alternative = Alternative().Match(text);
        if (!alternative.Success)
        {
            return new OwnerDecision(text, null, null);
        }

        var chosen = text[..alternative.Index].Trim();
        var rest = text[(alternative.Index + alternative.Length)..];
        var why = rest.IndexOf(" Why: ", StringComparison.Ordinal);
        return why < 0
            ? new OwnerDecision(chosen, rest.Trim(), null)
            : new OwnerDecision(chosen, rest[..why].Trim(), rest[(why + " Why: ".Length)..].Trim());
    }

    /// <summary>The text with the owner's mark taken out: the word, with a period or colon after it,
    /// and the bold left empty by it.</summary>
    private static string WithoutMark(string text)
    {
        var unmarked = OwnerMark().Replace(text, "**");
        return EmptyBold().Replace(unmarked, " ").Trim();
    }

    /// <summary>The text cut to <see cref="SummaryLength"/> characters with an ellipsis, never
    /// between the two halves of a character outside the basic plane.</summary>
    private static string Cut(string text)
    {
        if (text.Length <= SummaryLength)
        {
            return text;
        }

        var keep = SummaryLength - 1;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), "…");
    }

    [GeneratedRegex(@"\ACloses #(?<n>[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClosesLine();

    [GeneratedRegex(@" Alternatives?: ", RegexOptions.CultureInvariant)]
    private static partial Regex Alternative();

    // The mark as the agents write it: **owner-decision**, **owner-decision.** or
    // **owner-decision: what was decided**.
    [GeneratedRegex(@"\*\*owner-decision(?:[.:]\s*|(?=\*\*))", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerMark();

    [GeneratedRegex(@"\s*\*\*\*\*\s*", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyBold();

    /// <summary>Whether a line is inside a fenced block: one opened by three or more backticks or
    /// tildes, and closed only by a line of the same character, at least as long, with nothing
    /// after it, as the merge gate reads them.</summary>
    private sealed class Fence
    {
        private char _char;
        private int _length;

        /// <summary>Whether the line is a fence or inside one, moving past it.</summary>
        public bool Read(string line)
        {
            var trimmed = line.TrimStart();
            var run = 0;
            var c = trimmed.Length > 0 ? trimmed[0] : '\0';
            if (c is '`' or '~')
            {
                while (run < trimmed.Length && trimmed[run] == c)
                {
                    run++;
                }
            }

            if (_length > 0)
            {
                if (c == _char && run >= _length && string.IsNullOrWhiteSpace(trimmed[run..]))
                {
                    _length = 0;
                }

                return true;
            }

            if (run >= 3)
            {
                _char = c;
                _length = run;
                return true;
            }

            return false;
        }
    }
}