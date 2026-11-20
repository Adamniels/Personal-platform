using System.Globalization;
using System.Text;

namespace DevFlow.Protocol;

/// <summary>
/// The remerge diff a conflict job sends back and Dev flow keeps: <c>git show --remerge-diff</c> of
/// the merge commit, what the agent chose against what git would have done, and nothing for the
/// files that merged cleanly. Kept up to <see cref="MostBytes"/> of UTF-8, and cut beyond it at the
/// end of a line, with a line saying so, by the same rule on both sides, so a worker never sends
/// more than is kept. See "A conflict" in devflow/README.md.
/// </summary>
public static class RemergeDiffs
{
    /// <summary>The most of a remerge diff kept, in bytes of UTF-8: 1 MiB.</summary>
    public const int MostBytes = 1024 * 1024;

    /// <summary>The line a cut diff ends with, naming how long it was.</summary>
    public static string CutLine(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"[The remerge diff is cut here: it was {bytes} bytes, and the first {MostBytes} are kept.]\n");

    /// <summary><paramref name="diff"/> as it is when it fits in <see cref="MostBytes"/>; otherwise
    /// its whole lines that fit, then <see cref="CutLine"/>, the two together within the
    /// limit.</summary>
    public static string Cut(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var bytes = Encoding.UTF8.GetByteCount(diff);
        if (bytes <= MostBytes)
        {
            return diff;
        }

        var line = CutLine(bytes);
        var room = MostBytes - Encoding.UTF8.GetByteCount(line);
        var kept = new StringBuilder();
        var used = 0;
        foreach (var each in diff.Split('\n'))
        {
            var size = Encoding.UTF8.GetByteCount(each) + 1;
            if (used + size > room)
            {
                break;
            }

            kept.Append(each).Append('\n');
            used += size;
        }

        return kept.Append(line).ToString();
    }
}