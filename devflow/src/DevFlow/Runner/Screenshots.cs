namespace DevFlow.Runner;

/// <summary>Screenshots of one view, under the name they share, the desktop one first.</summary>
/// <param name="Name">The part of their names before <c>-desktop</c> and <c>-phone</c>, with
/// anything after it, such as <c>-before</c>, kept; a name with neither is its own.</param>
/// <param name="Images">The screenshots' names: the desktop one, then the phone one, either left
/// out when the body does not show it.</param>
public sealed record ScreenshotGroup(string Name, IReadOnlyList<string> Images);

/// <summary>
/// The screenshots of a pull request grouped into pairs as the page shows them, by the names
/// <c>capture()</c> in <c>e2e/conftest.py</c> gives them, <c>&lt;view&gt;-&lt;viewport&gt;</c>, and the
/// pick-up skill's before shots, <c>&lt;view&gt;-&lt;viewport&gt;-before</c>. Pure, like the reader.
/// </summary>
public static class Screenshots
{
    private const string DesktopPart = "desktop";
    private const string PhonePart = "phone";

    /// <summary>The names grouped by the part before <c>-desktop</c> and <c>-phone</c>, the last
    /// of either in a name, in the order each group first appears; within one, desktop, then
    /// phone, then a name with neither.</summary>
    public static IReadOnlyList<ScreenshotGroup> Group(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var groups = new List<(string Name, List<(string Image, int Rank)> Images)>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var (key, rank) = Split(name);
            var index = groups.FindIndex(group => string.Equals(group.Name, key, StringComparison.Ordinal));
            if (index < 0)
            {
                groups.Add((key, [(name, rank)]));
            }
            else
            {
                groups[index].Images.Add((name, rank));
            }
        }

        return [.. groups.Select(group => new ScreenshotGroup(group.Name, [.. group.Images.OrderBy(image => image.Rank).Select(image => image.Image)]))];
    }

    /// <summary>The name without its last <c>desktop</c> or <c>phone</c> part, and where it goes in
    /// its group; the name itself, last, when it has neither or is nothing else.</summary>
    private static (string Key, int Rank) Split(string name)
    {
        var parts = name.Split('-');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i] is DesktopPart or PhonePart && parts.Length > 1)
            {
                var key = string.Join('-', parts.Where((_, j) => j != i));
                return (key, parts[i] == DesktopPart ? 0 : 1);
            }
        }

        return (name, 2);
    }
}