namespace DevFlow.Merging;

/// <summary>
/// The patterns of a project's list of paths, <c>wait_paths</c>, the files whose change makes a
/// card's pull request wait for the owner on <c>sometimes</c>. See "The merge check" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class PathPatterns
{
    /// <summary>The most characters a pattern has.</summary>
    public const int Longest = 200;

    /// <summary>The most patterns a list holds.</summary>
    public const int Most = 100;

    /// <summary>
    /// What is wrong with <paramref name="pattern"/>, in words naming it, or null when nothing is:
    /// it is written, at most <see cref="Longest"/> characters, with no space at either end and no
    /// control character; it does not start with <c>!</c>, since there is no negation, nor with
    /// <c>/</c>, since every pattern is read from the repository's top; it separates folders with
    /// <c>/</c>, never <c>\</c>; and no folder in it is empty, <c>.</c> or <c>..</c>, which name no
    /// path GitHub lists. A pattern ending in <c>/</c> is a folder, everything under it.
    /// </summary>
    public static string? Problem(string? pattern)
    {
        if (pattern is null || string.IsNullOrWhiteSpace(pattern))
        {
            return "A pattern must not be empty";
        }

        if (pattern.Length > Longest)
        {
            return $"A pattern must be at most {Longest} characters";
        }

        if (pattern.Trim().Length != pattern.Length)
        {
            return $"{pattern} must not start or end with a space";
        }

        if (pattern.Any(char.IsControl))
        {
            return "A pattern must not hold a control character";
        }

        if (pattern.StartsWith('!'))
        {
            return $"{pattern} must not start with !: there is no negation";
        }

        if (pattern.StartsWith('/'))
        {
            return $"{pattern} must not start with /: every pattern is read from the repository's top";
        }

        if (pattern.Contains('\\', StringComparison.Ordinal))
        {
            return $"{pattern} must separate folders with /, not \\";
        }

        // A trailing slash marks a folder, so the empty segment after it is the one allowed.
        var folders = pattern.EndsWith('/') ? pattern[..^1].Split('/') : pattern.Split('/');
        return folders.Any(folder => folder is "" or "." or "..")
            ? $"{pattern} must not hold an empty, . or .. folder"
            : null;
    }
}