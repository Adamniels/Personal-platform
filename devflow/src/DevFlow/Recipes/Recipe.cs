using System.Text.RegularExpressions;

namespace DevFlow.Recipes;

/// <summary>An instruction a recipe may not hold, and the line it starts on, counted from 1.</summary>
public sealed record RefusedInstruction(int Line, string Instruction);

/// <summary>
/// A project's recipe, its <c>.devflow/Dockerfile</c>, read as Docker's parser reads it, far enough
/// to find its instructions: the <c>escape</c> parser directive at the top, a line ending in the
/// escape character continued on the next, and comment lines inside a continuation dropped. A
/// <c>COPY</c> or an <c>ADD</c>, in any case, alone or after <c>ONBUILD</c>, is refused: a worker's
/// image is built with no files of the project on the machine, and a project's packages are
/// installed by its check in each card's working copy. See "Workers" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
/// <remarks>
/// It errs on the side of refusing: a line inside a heredoc that begins with <c>COPY</c> is
/// refused too, though Docker would read it as the heredoc's text.
/// </remarks>
public static partial class Recipe
{
    private static readonly string[] Refused = ["COPY", "ADD"];

    /// <summary>The first instruction <paramref name="text"/> may not hold, or null when it holds
    /// none.</summary>
    public static RefusedInstruction? FirstRefused(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Docker drops a byte order mark at the start, so it hides no instruction.
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var escape = EscapeOf(lines);
        var joined = "";
        var start = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var continuing = start != 0;
            if (continuing && IsComment(line))
            {
                continue;
            }

            if (!continuing)
            {
                if (IsComment(line) || line.Trim().Length == 0)
                {
                    continue;
                }

                start = i + 1;
                joined = "";
            }

            var trimmed = line.TrimEnd();
            if (trimmed.EndsWith(escape))
            {
                joined += trimmed[..^1] + " ";
                continue;
            }

            joined += line;
            if (Check(joined, start) is { } refused)
            {
                return refused;
            }

            start = 0;
        }

        // A continuation the file ends inside is an instruction too.
        return start != 0 ? Check(joined, start) : null;
    }

    private static RefusedInstruction? Check(string instruction, int line)
    {
        var words = instruction.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return null;
        }

        var first = words[0];
        if (string.Equals(first, "ONBUILD", StringComparison.OrdinalIgnoreCase) && words.Length > 1)
        {
            first = words[1];
        }

        return Refused.FirstOrDefault(word => string.Equals(word, first, StringComparison.OrdinalIgnoreCase)) is { } refused
            ? new RefusedInstruction(line, refused)
            : null;
    }

    /// <summary>The parser directives Docker knows. Any other comment ends the directives.</summary>
    private static readonly string[] Directives = ["syntax", "escape", "check"];

    /// <summary>A parser directive's line, as BuildKit's parser matches it once leading whitespace
    /// is dropped.</summary>
    [GeneratedRegex(@"^#\s*([a-zA-Z][a-zA-Z0-9]*)\s*=\s*(.+?)\s*$")]
    private static partial Regex DirectiveLine();

    /// <summary>
    /// The escape character, from an <c>escape</c> parser directive at the very top, a backslash
    /// without one. Docker reads directives only up to the first line that is not a known one: an
    /// instruction, a blank line, or any other comment, including one shaped like a directive with
    /// a name it does not know. After that, <c># escape=`</c> is only a comment.
    /// </summary>
    private static char EscapeOf(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            var match = DirectiveLine().Match(line);
            if (!match.Success
                || !Directives.Contains(match.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.Equals(match.Groups[1].Value, "escape", StringComparison.OrdinalIgnoreCase))
            {
                // Docker refuses any other value, and a second escape directive, so the build
                // fails and the first one is all that matters.
                return match.Groups[2].Value is ['\\' or '`'] ? match.Groups[2].Value[0] : '\\';
            }
        }

        return '\\';
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');
}