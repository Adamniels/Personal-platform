using System.Globalization;
using System.Reflection;
using System.Text;

namespace DevFlow.Briefs;

/// <summary>The card a brief is for, as it was picked: the owner's words.</summary>
public sealed record BriefCard(int Number, string Title, string Body);

/// <summary>The project's facts a brief names: the check from its <c>.devflow/project.json</c>, the
/// branch the work goes on and the branch that is made from.</summary>
public sealed record BriefFacts(string Check, string Branch, string Base);

/// <summary>The commit a review brief is for, and where it was made: the card's branch and the
/// branch that is made from.</summary>
public sealed record ReviewFacts(string Commit, string Branch, string Base);

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
    public static string Developer(BriefCard card, BriefFacts facts)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(facts);
        // The placeholders are filled once each and in one pass, so a card holding the text of one
        // is never filled in turn.
        return Fill(DeveloperTemplate.Value, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["card"] = Fenced("text", Quoted(card)),
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
        });
    }

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