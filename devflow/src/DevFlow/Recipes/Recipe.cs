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
public static class Recipe
{
    private static readonly string[] Refused = ["COPY", "ADD"];

    /// <summary>The first instruction <paramref name="text"/> may not hold, or null when it holds
    /// none.</summary>
    public static RefusedInstruction? FirstRefused(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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

    /// <summary>The escape character, from an <c>escape</c> parser directive among the comment
    /// lines at the very top, a backslash without one. Docker reads directives only before the
    /// first line that is not one.</summary>
    private static char EscapeOf(string[] lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('#'))
            {
                break;
            }

            var directive = trimmed[1..].Split('=', 2);
            if (directive.Length != 2)
            {
                break;
            }

            if (string.Equals(directive[0].Trim(), "escape", StringComparison.OrdinalIgnoreCase)
                && directive[1].Trim() is { Length: 1 } value
                && value[0] is '\\' or '`')
            {
                return value[0];
            }
        }

        return '\\';
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');
}