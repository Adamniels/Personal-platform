using Npgsql;

namespace Review.Settings;

/// <summary>
/// A database's address as the platform writes it, <c>postgres://user:password@host:port/database</c>,
/// the form <c>scripts/agent/services.sh</c> builds, with the password percent encoded. Npgsql
/// reads only its own <c>key=value</c> form, so this turns one into the other, once, at startup.
/// </summary>
internal static class DatabaseUrl
{
    public const int DefaultPort = 5432;

    // The value holds a password, so no message here ever repeats it.
    private const string Expected = "is not a URL like postgres://user:password@host:5432/database";

    /// <summary>
    /// <paramref name="text"/> as Npgsql's settings: a <c>postgres</c> or <c>postgresql</c> scheme,
    /// a user and a password, a host, an optional port, 5432 when left out, and one path segment,
    /// the database. Nothing else: no query, no fragment, no whitespace.
    /// </summary>
    /// <exception cref="FormatException">It is not such a URL. The message never holds any of
    /// <paramref name="text"/>.</exception>
    public static NpgsqlConnectionStringBuilder Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!(text.StartsWith("postgres://", StringComparison.Ordinal) || text.StartsWith("postgresql://", StringComparison.Ordinal))
            || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            throw new FormatException(Expected);
        }

        var colon = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
        var user = colon < 0 ? "" : Uri.UnescapeDataString(uri.UserInfo[..colon]);
        var password = colon < 0 ? "" : Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
        var path = uri.AbsolutePath;
        var database = path.Length > 1 ? Uri.UnescapeDataString(path[1..]) : "";
        var port = uri.Port < 0 ? DefaultPort : uri.Port;
        if (user.Length == 0 || password.Length == 0 || database.Length == 0 || database.Contains('/', StringComparison.Ordinal) || port is < 1 or > 65535)
        {
            throw new FormatException(Expected);
        }

        return new NpgsqlConnectionStringBuilder
        {
            // Without the brackets an IPv6 host has in a URL.
            Host = uri.DnsSafeHost,
            Port = port,
            Username = user,
            Password = password,
            Database = database,
        };
    }
}