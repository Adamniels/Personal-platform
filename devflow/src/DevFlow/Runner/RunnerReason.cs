using System.Globalization;
using System.Text.RegularExpressions;

using DevFlow.CodeHost;

namespace DevFlow.Runner;

/// <summary>
/// Why a pull request is the owner's turn, in words, from the runner's mention of it,
/// <c>scripts/agent/run.sh</c>'s: on the slice issue when it hands a pull request over, with the
/// merge gate's reason lines in parentheses, or when the review rounds are used up; on the pull
/// request itself when the slice is Blocked. The latest such comment by the bot wins.
/// </summary>
internal static partial class RunnerReason
{
    internal const string NotSaid = "The runner did not say why.";

    private const string RoundsUsedUp = "The reviewer asked for changes a fourth time.";
    private const string Blocked = "The slice is Blocked.";

    /// <summary>The runner's words the reader recognises, each as <c>run.sh</c> writes it.</summary>
    internal static IReadOnlyList<string> Mentions { get; } =
    [
        "is approved and waits for your decision:",
        "is ready for you to read and merge:",
        "needs you, do not merge it as it is:",
        "has used up its review rounds",
        "is Blocked:",
    ];

    /// <summary>Each reason line <c>scripts/agent/merge-gate.sh</c> prints, as a sentence.</summary>
    private static readonly Dictionary<string, string> GateLines = new(StringComparer.Ordinal)
    {
        ["risk is not low"] = "High risk slice.",
        ["touches protected paths"] = "Touches paths that steer the agents.",
        ["an owner label is set"] = "Carries a decision for you.",
        ["ci-ok failed"] = "CI failed.",
        ["ci-ok is pending"] = "CI has not finished.",
        ["author is not the bot"] = "Not opened by the bot.",
        ["base is not main"] = "Does not merge into main.",
        ["pull request is a draft"] = "Is a draft.",
        ["head is from another repository"] = "Comes from another repository.",
        ["verdict is not approve"] = "The reviewer has not approved it.",
        ["verdict is not for the head commit"] = "The reviewer's approval is not for its latest commit.",
        ["slice is not the branch's planned issue"] = "Does not close the slice its branch names.",
        ["slice is not open"] = "The slice is not open.",
        ["risk record is not the owner's"] = "The slice's risk was not written by you alone.",
        ["pull request is already merged"] = "Already merged.",
    };

    /// <summary>The merge gate's reason line as a sentence, or the line as it is when the reader
    /// does not know it.</summary>
    internal static string Gate(string line) => GateLines.GetValueOrDefault(line, line);

    /// <summary>The reason from the latest mention of the pull request by the bot: a handover or
    /// the rounds used up on the slice, or a Blocked notice on the pull request.</summary>
    internal static string From(int pullRequest, IEnumerable<Comment?>? sliceComments, IEnumerable<Comment?>? pullRequestComments, string bot)
    {
        var number = pullRequest.ToString(CultureInfo.InvariantCulture);
        var said = (sliceComments ?? []).Select(comment => (Comment: comment, Reason: Handover(comment, number, bot)))
            .Concat((pullRequestComments ?? []).Select(comment => (Comment: comment, Reason: BlockedNotice(comment, bot))))
            .Where(read => read.Reason is not null)
            .OrderBy(read => read.Comment!.CreatedAt)
            .ThenBy(read => read.Comment!.Id)
            .LastOrDefault();
        return said.Reason ?? NotSaid;
    }

    /// <summary>The reason in a mention on the slice handing this pull request over, or null when
    /// the comment is not one.</summary>
    private static string? Handover(Comment? comment, string number, string bot)
    {
        if (!ByBot(comment, bot))
        {
            return null;
        }

        var rounds = RoundsMention().Match(comment!.Body);
        if (rounds.Success && rounds.Groups["pr"].Value == number)
        {
            return RoundsUsedUp;
        }

        var gate = GateMention().Match(comment.Body);
        if (!gate.Success || gate.Groups["pr"].Value != number)
        {
            return null;
        }

        var lines = gate.Groups["reasons"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : string.Join(' ', lines.Select(Gate));
    }

    /// <summary>The reason in a Blocked notice on the pull request: "The slice is Blocked."
    /// followed by the notice's first sentence after its colon; null when the comment is not one.</summary>
    private static string? BlockedNotice(Comment? comment, string bot)
    {
        if (!ByBot(comment, bot) || BlockedMention().Match(comment!.Body) is not { Success: true } match)
        {
            return null;
        }

        var rest = match.Groups["rest"].Value;
        var end = SentenceEnd().Match(rest);
        var sentence = (end.Success ? rest[..(end.Index + 1)] : rest).Trim();
        return sentence.Length == 0 ? Blocked : $"{Blocked} {sentence}";
    }

    private static bool ByBot(Comment? comment, string bot) =>
        comment?.Body is not null && string.Equals(comment.Author, bot, StringComparison.Ordinal);

    // Each mention starts the comment, after the owner's @login, so a reply quoting one is not one.
    [GeneratedRegex(@"\A(?:@\S+ )?PR #(?<pr>[0-9]+) (?:is approved and waits for your decision|is ready for you to read and merge|needs you, do not merge it as it is): [^(]*\((?<reasons>.*)\)\.\s*\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex GateMention();

    [GeneratedRegex(@"\A(?:@\S+ )?PR #(?<pr>[0-9]+) has used up its review rounds\b", RegexOptions.CultureInvariant)]
    private static partial Regex RoundsMention();

    [GeneratedRegex(@"\A(?:@\S+ )?slice #[0-9]+ is Blocked: (?<rest>.*)\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex BlockedMention();

    // A period followed by white space or the end.
    [GeneratedRegex(@"\.(?=\s|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();
}