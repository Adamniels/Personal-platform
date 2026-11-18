using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DevFlow.Protocol;

/// <summary>
/// What a build's pictures came to, in its result: the names of the pictures the worker took and
/// pushed to <see cref="PageShots.Branch"/>, in the report's order, desktop then phone for each
/// page, and a note when there is something to say instead or as well. Null on a result for a run
/// that took none, a check that failed among them, and from a worker that sends none.
/// </summary>
/// <param name="Names">Each picture's name, <c>&lt;page&gt;-desktop</c> or <c>&lt;page&gt;-phone</c>,
/// at <see cref="PageShots.Folder"/> on <see cref="PageShots.Branch"/>; empty when none was
/// pushed.</param>
/// <param name="Note">"This project has no pages to show", "The report names no pages", or
/// "Screenshots could not be taken: &lt;why&gt;"; null when every page was taken.</param>
public sealed record ResultScreenshots(IReadOnlyList<string>? Names, string? Note);

/// <summary>
/// The rules a build's pictures follow, the worker's and Dev flow's alike: which pages are taken,
/// what each picture is named, at which sizes, and where it goes. See "Screenshots" in
/// devflow/README.md.
/// </summary>
public static partial class PageShots
{
    /// <summary>The branch the pictures are committed to, an orphan one, never the default.</summary>
    public const string Branch = "pr-screenshots";

    /// <summary>The most pages taken, the report's first ones.</summary>
    public const int MostPages = 10;

    /// <summary>The longest a page's part of a name may be.</summary>
    public const int MostNameLength = 60;

    /// <summary>The longest a note may be.</summary>
    public const int MostNote = 1000;

    /// <summary>What the note says when the project has no <c>start</c> or <c>url</c>.</summary>
    public const string NoPages = "This project has no pages to show";

    /// <summary>What the note says when the report names no page.</summary>
    public const string NoPagesNamed = "The report names no pages";

    /// <summary>What the note begins with when a picture was not taken.</summary>
    public const string CouldNot = "Screenshots could not be taken: ";

    /// <summary>The two sizes each page is taken at, full page: the desktop's and a phone's.</summary>
    public static IReadOnlyList<Viewport> Viewports { get; } = [new("desktop", 1280, 800), new("phone", 390, 844)];

    /// <summary>The folder a commit's pictures are in on <see cref="Branch"/>:
    /// <c>devflow/&lt;card&gt;/&lt;the commit's first seven characters&gt;</c>.</summary>
    public static string Folder(int card, string commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return string.Create(CultureInfo.InvariantCulture, $"devflow/{card}/{commit[..Math.Min(7, commit.Length)]}");
    }

    /// <summary>
    /// A page's part of its pictures' names: its path lowercased, each run of anything but ASCII
    /// letters and digits made one hyphen, hyphens trimmed from both ends, cut to
    /// <see cref="MostNameLength"/> characters and trimmed again; <c>home</c> when nothing is left,
    /// as for <c>/</c>.
    /// </summary>
    public static string NameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = new StringBuilder(path.Length);
        foreach (var c in path.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                name.Append(c);
            }
            else if (name.Length > 0 && name[^1] != '-')
            {
                name.Append('-');
            }
        }

        var cut = name.ToString().Trim('-');
        cut = cut[..Math.Min(MostNameLength, cut.Length)].TrimEnd('-');
        return cut.Length == 0 ? "home" : cut;
    }

    /// <summary>Whether <paramref name="name"/> is a picture's name these rules make: a page's
    /// part, then <c>-desktop</c> or <c>-phone</c>.</summary>
    public static bool IsName(string? name) =>
        name is not null && PictureName().Match(name) is { Success: true } match && match.Groups["page"].Length <= MostNameLength;

    [GeneratedRegex(@"^(?<page>[a-z0-9]+(-[a-z0-9]+)*)-(desktop|phone)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PictureName();
}

/// <summary>A size a page is taken at, named for the end of its pictures' names.</summary>
public sealed record Viewport(string Name, int Width, int Height);