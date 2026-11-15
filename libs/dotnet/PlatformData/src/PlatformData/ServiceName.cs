using System.Text.RegularExpressions;

namespace PlatformData;

/// <summary>
/// A service's name as this library takes it: <c>^[a-z][a-z0-9_]*$</c>, such as <c>review</c>.
/// It names the door's setting, <c>&lt;name&gt;.user_id</c>, which goes into SQL as an identifier,
/// so it is checked wherever it is taken, before it reaches a statement, and never quoted or
/// escaped instead. The same name is the prefix of the service's roles, <c>&lt;name&gt;_owner</c>
/// and <c>&lt;name&gt;_app</c>, as <c>infra/services.json</c> names them.
/// </summary>
internal static partial class ServiceName
{
    /// <summary><paramref name="name"/>, if it is a service's name.</summary>
    /// <exception cref="ArgumentException">It is not.</exception>
    public static string Check(string name, string parameter)
    {
        ArgumentNullException.ThrowIfNull(name, parameter);
        return Pattern().IsMatch(name)
            ? name
            : throw new ArgumentException("a service's name is lowercase ASCII letters, digits and underscores, starting with a letter", parameter);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}