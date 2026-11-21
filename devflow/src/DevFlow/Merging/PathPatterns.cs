using DevFlow.CodeHost;

using Microsoft.Extensions.FileSystemGlobbing;

namespace DevFlow.Merging;

/// <summary>A file a pull request changes that a pattern on the list matches: the path matched, a
/// renamed file's new or old one, and the pattern, as the owner wrote it.</summary>
public sealed record PathMatch(string Path, string Pattern);

/// <summary>
/// The patterns of a project's list of paths, <c>wait_paths</c>, the files whose change makes a
/// card's pull request wait for the owner on <c>sometimes</c>: globs as
/// <c>Microsoft.Extensions.FileSystemGlobbing</c>'s <c>Matcher</c> reads them, <c>*</c> within a
/// folder, <c>**</c> across folders, and a pattern ending in <c>/</c> meaning everything under it,
/// each read from the repository's top, with no negation. Paths are matched whatever their case,
/// so a list never misses a file by its case, the stricter of the two. Pure. See "The merge check"
/// in devflow/docs/devflow-architecture.md.
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

    /// <summary>Whether <paramref name="pattern"/>, one the list may hold, matches
    /// <paramref name="path"/>, a path as the code host lists it.</summary>
    public static bool Matches(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        return Of(pattern).Match(path).HasMatches;
    }

    /// <summary>
    /// The first file of <paramref name="files"/>, in their order, that a pattern of
    /// <paramref name="patterns"/> matches, by its new path, then by its old one when it was
    /// renamed, with the first pattern, in the list's order, that matches it; or null when none
    /// does.
    /// </summary>
    public static PathMatch? FirstMatch(IReadOnlyList<string> patterns, IReadOnlyList<ChangedFile> files)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(files);
        var matchers = patterns.Select(pattern => (Pattern: pattern, Matcher: Of(pattern))).ToList();
        foreach (var file in files)
        {
            foreach (var path in file.PreviousPath is { } previous ? [file.Path, previous] : new[] { file.Path })
            {
                if (matchers.FirstOrDefault(each => each.Matcher.Match(path).HasMatches) is { Matcher: not null } matched)
                {
                    return new PathMatch(path, matched.Pattern);
                }
            }
        }

        return null;
    }

    /// <summary>The matcher of one pattern, whatever the case; a folder, a pattern ending in
    /// <c>/</c>, is everything under it.</summary>
    private static Matcher Of(string pattern)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern.EndsWith('/') ? pattern + "**" : pattern);
        return matcher;
    }
}