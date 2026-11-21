using System.Text.RegularExpressions;

namespace DevFlow.Projects;

/// <summary>One login on a project's list of trusted accounts, as the settings route answers
/// it.</summary>
/// <param name="Login">The GitHub login.</param>
/// <param name="Mine">Whether it is the caller's own linked login, which is always on the list and
/// cannot be removed.</param>
public sealed record TrustedLogin(string Login, bool Mine);

/// <summary>
/// A project's trusted accounts: the GitHub logins whose cards, edits and comments are instructions,
/// and the only place that answers whether a login is one. The owner's linked login is always on
/// the list without being stored, so a relinked account is trusted under its new login at once and
/// the old one is not; without a link nobody is known to be the owner, and nobody is trusted. Logins
/// are one account whatever their case, as GitHub's are. A trusted account gives instructions and
/// never makes Dev flow act as it: acting as the owner stays his linked token's alone. See "Cards"
/// in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class TrustedAccounts
{
    /// <summary>The most characters a GitHub login has.</summary>
    public const int LongestLogin = 39;

    private TrustedAccounts(string? mine, IReadOnlyList<string> others)
    {
        Mine = mine;
        Others = others;
    }

    /// <summary>The owner's linked login, null without a link.</summary>
    public string? Mine { get; }

    /// <summary>The others, as stored: never the owner's own, one per login whatever its case, in
    /// the order he gave them.</summary>
    public IReadOnlyList<string> Others { get; }

    /// <summary>The list with the owner's linked login first, marked as his, then the
    /// others.</summary>
    public IReadOnlyList<TrustedLogin> Listed =>
        [.. Mine is null ? [] : new[] { new TrustedLogin(Mine, true) }, .. Others.Select(login => new TrustedLogin(login, false))];

    /// <summary>
    /// The list of a project whose owner's linked login is <paramref name="mine"/> and whose stored
    /// others are <paramref name="others"/>: his own dropped from the others, whatever its case, and
    /// a login given twice kept once, in its first spelling.
    /// </summary>
    public static TrustedAccounts Of(string? mine, IEnumerable<string> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        var kept = new List<string>();
        foreach (var login in others)
        {
            if (!Same(login, mine) && !kept.Any(seen => Same(seen, login)))
            {
                kept.Add(login);
            }
        }

        return new TrustedAccounts(mine, kept);
    }

    /// <summary>Whether <paramref name="login"/>'s words count: it is on the list, whatever its
    /// case, and the owner has a link.</summary>
    public bool Trusts(string login) =>
        Mine is not null && (Same(login, Mine) || Others.Any(other => Same(other, login)));

    /// <summary>Whether <paramref name="value"/> has the shape of a GitHub login: 1 to
    /// <see cref="LongestLogin"/> characters of ASCII letters, digits and single hyphens, with no
    /// hyphen at either end.</summary>
    public static bool IsLogin(string value) =>
        value.Length <= LongestLogin && LoginShape().IsMatch(value);

    private static bool Same(string login, string? other) => string.Equals(login, other, StringComparison.OrdinalIgnoreCase);

    // \z, not $, which would let a trailing newline through.
    [GeneratedRegex(@"^[A-Za-z0-9]+(-[A-Za-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex LoginShape();
}