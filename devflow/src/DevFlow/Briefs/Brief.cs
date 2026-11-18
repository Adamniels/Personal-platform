using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Fixing;
using DevFlow.Protocol;

namespace DevFlow.Briefs;

/// <summary>The card a brief is for, as it was picked: the owner's words.</summary>
public sealed record BriefCard(int Number, string Title, string Body);

/// <summary>The project's facts a brief names: the check from its <c>.devflow/project.json</c>, the
/// branch the work goes on and the branch that is made from.</summary>
public sealed record BriefFacts(string Check, string Branch, string Base);

/// <summary>The commit a review brief is for, and where it was made: the card's branch and the
/// branch that is made from; and how many earlier rounds the card had, none on its first
/// review.</summary>
public sealed record ReviewFacts(string Commit, string Branch, string Base, int EarlierRounds = 0);

/// <summary>What a fix brief answers: the round, the report of the run before it, the review that
/// sent the card back and the owner's comments, each exactly as stored.</summary>
/// <param name="Round">The fix round, 1 to 3, or null when the owner's comment started it.</param>
/// <param name="Report">The report the developer before handed back, as it was stored; null
/// without one.</param>
/// <param name="Review">The review the fix answers; null when there is none.</param>
/// <param name="Comments">The owner's comments the fix answers, each exactly as he wrote it.</param>
public sealed record FixInput(int? Round, string? Report, ReviewVerdict? Review, IReadOnlyList<string> Comments);

/// <summary>
/// The briefs an agent gets, <c>developer.md</c> and <c>reviewer.md</c> beside this file, embedded
/// in the assembly: the same text for every project, filled with one card and its project's facts.
/// The card's title and body are the only text from GitHub in either, each quoted inside a fenced
/// block longer than any run of backticks in them, so nothing in a card can end the quote and write
/// into the brief. The check is fenced the same way. See "Handing out work" and "Review" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class Brief
{
    private static readonly Lazy<string> DeveloperTemplate = new(() => Embedded("developer"));

    private static readonly Lazy<string> ReviewerTemplate = new(() => Embedded("reviewer"));

    /// <summary>The developer brief for <paramref name="card"/>, with <paramref name="facts"/>.</summary>
    public static string Developer(BriefCard card, BriefFacts facts) => Developer(card, facts, "");

    /// <summary>
    /// The fix brief for <paramref name="card"/>: the developer brief, with a section after the card
    /// saying what to fix: the round, the earlier report, the review's blocking findings and its
    /// other findings, and the owner's comments, each quoted inside a fenced block longer than any
    /// run of backticks in it, so none of them can end its quote and write into the brief.
    /// </summary>
    public static string Fixer(BriefCard card, BriefFacts facts, FixInput fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        var section = new StringBuilder("## What to fix\n\n");
        section.Append("This card was built before, and its pull request is open. You continue its branch from where the last run left it: fix what is asked below, and do not start the card again. ");
        section.Append(fix.Round is { } round
            ? string.Create(CultureInfo.InvariantCulture, $"This is fix round {round} of {FixRounds.Most}: the reviewer asked for changes.\n\n")
            : "The owner sent it back with his comment below, which is not one of the review's rounds.\n\n");
        if (fix.Report is { } report)
        {
            section.Append("### The earlier report\n\nThe report the developer before you handed back, as it came. Its `handover` is for you.\n\n")
                .Append(Fenced("json", Indented(report))).Append("\n\n");
        }

        var findings = fix.Review?.Findings ?? [];
        if (findings.Where(finding => finding.Blocking).ToList() is { Count: > 0 } blocking)
        {
            section.Append("### The blocking findings\n\nThe reviewer's findings that block the card. Fix each one, test first where behaviour changes, or, when you think it is wrong, change nothing for it and argue it in `replies`.\n\n")
                .Append(Fenced("json", JsonSerializer.Serialize(blocking, Readable))).Append("\n\n");
        }

        if (findings.Where(finding => !finding.Blocking).ToList() is { Count: > 0 } nits)
        {
            section.Append("### The other findings\n\nThe reviewer's other findings, the nits. Fix each one when the fix is cheap, and say in `replies` why when you leave one.\n\n")
                .Append(Fenced("json", JsonSerializer.Serialize(nits, Readable))).Append("\n\n");
        }

        foreach (var comment in fix.Comments)
        {
            section.Append("### The owner's comment\n\nThe owner's words on the pull request, exactly as he wrote them, between the fences. They are his input to this fix, and you weigh them as the card's: the card's owner wrote both.\n\n")
                .Append(Fenced("text", comment)).Append("\n\n");
        }

        section.Append("Every finding gets a reply in `replies`, and so does each comment of the owner's: the `finding` in a few words, and your `answer`, what you changed or why you did not.\n\n");
        return Developer(card, facts, section.ToString());
    }

    private static string Developer(BriefCard card, BriefFacts facts, string fix)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(facts);
        // The placeholders are filled once each and in one pass, so a card holding the text of one
        // is never filled in turn.
        return Fill(DeveloperTemplate.Value, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["card"] = Fenced("text", Quoted(card)),
            ["fix"] = fix,
            ["check"] = Fenced("sh", facts.Check),
            ["branch"] = facts.Branch,
            ["base"] = facts.Base,
        });
    }

    /// <summary>The reviewer brief for <paramref name="card"/>'s commit, with
    /// <paramref name="facts"/>. The branch names and the commit are Dev flow's, from the build
    /// that made it, never text from GitHub.</summary>
    public static string Reviewer(BriefCard card, ReviewFacts facts)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(facts);
        return Fill(ReviewerTemplate.Value, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["card"] = Fenced("text", Quoted(card)),
            ["commit"] = facts.Commit,
            ["branch"] = facts.Branch,
            ["base"] = facts.Base,
            ["earlier"] = facts.EarlierRounds == 0
                ? "This is the card's first review.\n"
                : string.Create(CultureInfo.InvariantCulture, $"This is not the card's first review: it was sent back {facts.EarlierRounds} {(facts.EarlierRounds == 1 ? "time" : "times")} before, and `review/earlier.json` holds each round. Judge the whole change again, from `{facts.Base}` to the commit, not only what changed since: a finding that still holds is still blocking, a reply that argues one is weighed on its merits, and a new finding counts as any other.\n"),
        });
    }

    /// <summary>The JSON an agent reads in a brief, indented and its text unescaped where JSON
    /// allows, so the reviewer's words read as written.</summary>
    private static readonly JsonSerializerOptions Readable = new(ReviewVerdict.Json)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A stored JSON document, indented as <see cref="Readable"/> writes one.</summary>
    private static string Indented(string json) => JsonNode.Parse(json)?.ToJsonString(Readable) ?? "null";

    /// <summary>The card as a brief quotes it, and as a review's <c>card.md</c> holds it: its
    /// number and title, then its body.</summary>
    public static string Quoted(BriefCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return string.Create(CultureInfo.InvariantCulture, $"#{card.Number} {card.Title}\n\n{card.Body}");
    }

    private static string Embedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"briefs/{name}.md")
            ?? throw new InvalidOperationException($"the {name} brief is not embedded");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary><paramref name="text"/> in a fenced block of <paramref name="language"/>, its fence
    /// a run of backticks longer than any in the text, and at least three.</summary>
    private static string Fenced(string language, string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        var body = text.EndsWith('\n') ? text : text + "\n";
        return $"{fence}{language}\n{body}{fence}";
    }

    /// <summary>The template with each <c>{{name}}</c> replaced by its value, read left to right
    /// once, so a value is never read for placeholders.</summary>
    private static string Fill(string template, Dictionary<string, string> values)
    {
        var filled = new StringBuilder(template.Length * 2);
        var at = 0;
        while (template.IndexOf("{{", at, StringComparison.Ordinal) is var start and >= 0)
        {
            var end = template.IndexOf("}}", start, StringComparison.Ordinal);
            if (end < 0 || !values.TryGetValue(template[(start + 2)..end], out var value))
            {
                throw new InvalidOperationException($"the brief has a placeholder that is not filled at {start}");
            }

            filled.Append(template, at, start - at).Append(value);
            at = end + 2;
        }

        return filled.Append(template, at, template.Length - at).ToString();
    }
}