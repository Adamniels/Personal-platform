using System.Globalization;

using Npgsql;

using PlatformData;

namespace DevFlow.Settings;

/// <summary>
/// Dev flow's settings, read from the process environment once, at startup, by
/// <see cref="Load"/>. Unset is the default, and a value that is set must parse, so a typo stops
/// Dev flow at startup instead of quietly becoming the default; the rules are review's, in
/// review/src/Review/Settings/ReviewSettings.cs, and canary's before it, written again rather than
/// shared, since no service imports from another's directory. Dev flow loads no .env file. See
/// "Settings" in devflow/README.md.
/// </summary>
/// <param name="ListenHost">The host Kestrel binds, an IPv6 address in brackets.</param>
/// <param name="ListenPort">The port Kestrel binds.</param>
/// <param name="CoreUrl">The core's own address, never the proxy's, where the key set is fetched.</param>
/// <param name="PublicOrigin">The platform's origin as a browser sends it, for the <c>Origin</c> check.</param>
/// <param name="ExposeOpenApi">Whether <c>GET /devflow/api/openapi.json</c> is mapped, for the
/// contract test and regenerating the schema only.</param>
/// <param name="AppDatabase">The database as devflow_app, from <c>DEVFLOW_APP_DATABASE_URL</c>,
/// required, with no default. It holds a password, so <see cref="ToString"/> leaves it out, and
/// it is read only, by convention, since Npgsql's settings type is not.</param>
/// <param name="CodeHost">The code host, from <c>DEVFLOW_CODE_HOST</c>, <c>fake</c> unless set: which
/// <see cref="CodeHost.ICodeHost"/> the app is built with, whether the fake's addresses are mapped,
/// and whether the vault needs a key. Set and empty is refused like any value that does not parse,
/// so the hosted copy, which passes it with <c>${DEVFLOW_CODE_HOST:-}</c>, cannot fall back to the
/// fake by leaving it out.</param>
/// <param name="Vault">The vault's keys, from <c>DEVFLOW_VAULT_KEY</c> and
/// <c>DEVFLOW_VAULT_OLD_KEYS</c>, see <see cref="VaultSettings"/>. They are secret, so
/// <see cref="ToString"/> names their ids alone.</param>
/// <param name="GitHub">The GitHub App, from the four <c>DEVFLOW_GITHUB_</c> settings, in the
/// GitHub mode, and null in the fake one, which reads none of them; see <see cref="GitHubSettings"/>.
/// It holds a secret and a private key, so <see cref="ToString"/> names the client id and the slug
/// alone.</param>
public sealed record DevFlowSettings(
    string ListenHost,
    int ListenPort,
    Uri CoreUrl,
    string PublicOrigin,
    bool ExposeOpenApi,
    NpgsqlConnectionStringBuilder AppDatabase,
    CodeHostMode CodeHost,
    VaultSettings Vault,
    GitHubSettings? GitHub)
{
    public const string DefaultListenAddr = "127.0.0.1:8084";
    public const string DefaultCoreUrl = "http://127.0.0.1:8080";
    public const string DefaultPublicOrigin = "https://platform.test";

    /// <summary>
    /// The name the shared database code in libs/dotnet/PlatformData/ knows Dev flow by: the door's
    /// setting, <c>devflow.user_id</c>, which every policy reads, the migration lock's key, and the
    /// roles, <c>devflow_owner</c> and <c>devflow_app</c>. Changing it would orphan every policy
    /// already applied.
    /// </summary>
    public const string Service = "devflow";

    /// <summary>The app role's database URL, which serving requires.</summary>
    public const string AppDatabaseUrl = "DEVFLOW_APP_DATABASE_URL";

    /// <summary>The owner's database URL, which <c>migrate</c> reads and nothing else does.</summary>
    public const string OwnerDatabaseUrl = "DEVFLOW_OWNER_DATABASE_URL";

    /// <summary>The address Kestrel listens on.</summary>
    public string ListenUrl => $"http://{ListenHost}:{ListenPort.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The settings from <paramref name="read"/>, which gives a variable's value or null when it
    /// is unset, as <see cref="Environment.GetEnvironmentVariable(string)"/> does.
    /// </summary>
    /// <exception cref="StartupException">A setting is set and does not parse; the message
    /// starts with its name.</exception>
    public static DevFlowSettings Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var (host, port) = Setting(read, "DEVFLOW_LISTEN_ADDR", DefaultListenAddr, ParseListenAddr);
        var codeHost = Setting(read, "DEVFLOW_CODE_HOST", "fake", ParseCodeHost);
        return new DevFlowSettings(
            host,
            port,
            Setting(read, "DEVFLOW_CORE_URL", DefaultCoreUrl, ParseCoreUrl),
            Setting(read, "DEVFLOW_PUBLIC_ORIGIN", DefaultPublicOrigin, ParseOrigin),
            Setting(read, "DEVFLOW_EXPOSE_OPENAPI", "0", ParseSwitch),
            Setting(read, AppDatabaseUrl, fallback: null, DatabaseUrl.Parse),
            codeHost,
            VaultSettings.Load(read, codeHost),
            GitHubSettings.Load(read, codeHost));
    }

    /// <summary>What the record prints, without the database's password, the vault's keys or the
    /// GitHub App's secret and key, which it holds.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"DevFlowSettings {{ ListenHost = {ListenHost}, ListenPort = {ListenPort}, CoreUrl = {CoreUrl}, PublicOrigin = {PublicOrigin}, ExposeOpenApi = {ExposeOpenApi}, AppDatabase = {AppDatabase.Username}@{AppDatabase.Host}:{AppDatabase.Port}/{AppDatabase.Database}, CodeHost = {CodeHost}, Vault = {Vault}, GitHub = {GitHub?.ToString() ?? "none"} }}");

    /// <summary>The setting parsed, or <paramref name="fallback"/> parsed when it is unset; with
    /// no fallback, unset is refused.</summary>
    private static T Setting<T>(Func<string, string?> read, string name, string? fallback, Func<string, T> parse)
    {
        var value = read(name) ?? fallback ?? throw new StartupException($"{name}: is not set, and Dev flow cannot serve without it");
        try
        {
            return parse(value);
        }
        catch (FormatException refused)
        {
            // The other parsers repeat the value, to help, but DatabaseUrl.Parse never does, since
            // a database URL holds a password.
            throw new StartupException($"{name}: {refused.Message}");
        }
    }

    /// <summary>A host and a port, such as <c>127.0.0.1:8084</c>, an IPv6 host in brackets.</summary>
    internal static (string Host, int Port) ParseListenAddr(string text)
    {
        var colon = text.LastIndexOf(':');
        var host = colon < 0 ? "" : text[..colon];
        if (colon < 0 || !IsHost(host) || ParsePort(text[(colon + 1)..]) is not { } port)
        {
            throw new FormatException($"'{text}' is not a host and a port like 127.0.0.1:8084");
        }

        return (host, port);
    }

    /// <summary>A scheme, a host and an optional port, with nothing after it, such as
    /// <c>http://127.0.0.1:8080</c>, so that the key set's path can be appended.</summary>
    internal static Uri ParseCoreUrl(string text)
    {
        if (Split(text) is null)
        {
            throw new FormatException(
                $"'{text}' is not a URL like http://127.0.0.1:8080: scheme, host and optional port, nothing after it");
        }

        return new Uri(text, UriKind.Absolute);
    }

    /// <summary>Only the form a browser sends: scheme and host, lowercase, with a port only when
    /// it is not the scheme's default, and nothing after it. Anything else would never match and
    /// silently refuse every request that changes state. The same rule as
    /// <c>PublicOrigin::parse</c> in <c>Core/src/origin.rs</c>.</summary>
    internal static string ParseOrigin(string text)
    {
        if (Split(text) is not { } parts
            || parts.Port == (parts.Scheme == "https" ? 443 : 80)
            || !string.Equals(text, text.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new FormatException(
                $"'{text}' is not an origin like https://platform.test: scheme and host, lowercase, no path, no default port");
        }

        return text;
    }

    /// <summary><c>fake</c> or <c>github</c>, and nothing else.</summary>
    internal static CodeHostMode ParseCodeHost(string text) => text switch
    {
        "fake" => CodeHostMode.Fake,
        "github" => CodeHostMode.GitHub,
        _ => throw new FormatException($"'{text}' is not fake or github"),
    };

    /// <summary><c>1</c> for on, <c>0</c> for off, and nothing else.</summary>
    internal static bool ParseSwitch(string text) => text switch
    {
        "1" => true,
        "0" => false,
        _ => throw new FormatException($"'{text}' is not 1 or 0"),
    };

    /// <summary>
    /// <paramref name="text"/> split, if it is exactly <c>scheme://host[:port]</c> with an http or
    /// https scheme: no path, query, fragment, user info, trailing colon or whitespace.
    /// </summary>
    private static (string Scheme, string Host, int? Port)? Split(string text)
    {
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var scheme = text[..separator].ToLowerInvariant();
        var authority = text[(separator + 3)..];
        if (scheme is not ("http" or "https") || authority.IndexOfAny(['/', '?', '#', '@']) >= 0)
        {
            return null;
        }

        // The port follows the last colon after the host, which for an IPv6 host is after its ']'.
        var colon = authority.LastIndexOf(':');
        if (colon >= 0 && colon < authority.LastIndexOf(']'))
        {
            colon = -1;
        }

        var host = colon < 0 ? authority : authority[..colon];
        if (!IsHost(host))
        {
            return null;
        }

        if (colon < 0)
        {
            return (scheme, host, null);
        }

        return ParsePort(authority[(colon + 1)..]) is { } port ? (scheme, host, port) : null;
    }

    /// <summary>A DNS name, an IPv4 address, or an IPv6 address in brackets.</summary>
    private static bool IsHost(string host) => Uri.CheckHostName(host) switch
    {
        UriHostNameType.Dns or UriHostNameType.IPv4 => true,
        UriHostNameType.IPv6 => host.StartsWith('[') && host.EndsWith(']'),
        _ => false,
    };

    /// <summary>One to five ASCII digits, from 1 to 65535, or null.</summary>
    private static int? ParsePort(string text) =>
        text.Length is >= 1 and <= 5
        && text.All(char.IsAsciiDigit)
        && int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) is var port and >= 1 and <= 65535
            ? port
            : null;
}