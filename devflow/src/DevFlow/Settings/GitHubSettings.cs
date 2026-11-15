using System.Security.Cryptography;

using PlatformData;

namespace DevFlow.Settings;

/// <summary>
/// Dev flow's GitHub App, from <c>DEVFLOW_GITHUB_CLIENT_ID</c>, <c>DEVFLOW_GITHUB_CLIENT_SECRET</c>,
/// <c>DEVFLOW_GITHUB_PRIVATE_KEY_FILE</c> and <c>DEVFLOW_GITHUB_APP_SLUG</c>, read by
/// <see cref="DevFlowSettings.Load"/>. Each is required with <c>DEVFLOW_CODE_HOST=github</c> and
/// ignored otherwise. The key is read from its file once, at startup. The secret and the slug are
/// read here and first used by signing a user in, a later slice. See "Settings" in
/// devflow/README.md and "GitHub" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class GitHubSettings
{
    public const string ClientIdSetting = "DEVFLOW_GITHUB_CLIENT_ID";
    public const string ClientSecretSetting = "DEVFLOW_GITHUB_CLIENT_SECRET";
    public const string PrivateKeyFileSetting = "DEVFLOW_GITHUB_PRIVATE_KEY_FILE";
    public const string AppSlugSetting = "DEVFLOW_GITHUB_APP_SLUG";

    /// <summary>The shortest key taken. GitHub makes 2048 bit keys.</summary>
    private const int MinimumKeyBits = 2048;

    private GitHubSettings(string clientId, string clientSecret, RSA privateKey, string appSlug)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
        PrivateKey = privateKey;
        AppSlug = appSlug;
    }

    /// <summary>The app's client id, such as <c>Iv23li...</c>, which the app's tokens carry as
    /// <c>iss</c>.</summary>
    public string ClientId { get; }

    /// <summary>The app's client secret, for signing a user in. Never printed.</summary>
    public string ClientSecret { get; }

    /// <summary>The app's private key, which signs the app's tokens. Never printed, and never
    /// exported by Dev flow.</summary>
    public RSA PrivateKey { get; }

    /// <summary>The app's name in its GitHub address, <c>github.com/apps/&lt;slug&gt;</c>.</summary>
    public string AppSlug { get; }

    /// <summary>
    /// The app's settings in the GitHub mode, and null in any other, which reads none of them.
    /// Each setting empty is the same as unset, since the hosted copy passes each with
    /// <c>${VAR:-}</c>.
    /// </summary>
    /// <exception cref="StartupException">One is unset, empty or malformed, or the key file cannot
    /// be read or holds no RSA private key in PEM. The message starts with the setting's name and
    /// never repeats its value, the key file's path included.</exception>
    internal static GitHubSettings? Load(Func<string, string?> read, CodeHostMode codeHost)
    {
        if (codeHost != CodeHostMode.GitHub)
        {
            return null;
        }

        var clientId = Required(read, ClientIdSetting);
        if (!clientId.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))
        {
            throw new StartupException($"{ClientIdSetting}: is not a client id, which is ASCII letters, digits and dots, as the app's GitHub page shows it");
        }

        var clientSecret = Required(read, ClientSecretSetting);
        if (!clientSecret.All(c => c is > ' ' and <= '~'))
        {
            throw new StartupException($"{ClientSecretSetting}: is not a client secret, which is printable ASCII without spaces, as GitHub gives it");
        }

        var privateKey = ReadKey(Required(read, PrivateKeyFileSetting));

        var appSlug = Required(read, AppSlugSetting);
        if (!appSlug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') || appSlug.StartsWith('-') || appSlug.EndsWith('-'))
        {
            privateKey.Dispose();
            throw new StartupException($"{AppSlugSetting}: is not an app's slug, which is lowercase letters, digits and inner hyphens, as in github.com/apps/<slug>");
        }

        return new GitHubSettings(clientId, clientSecret, privateKey, appSlug);
    }

    /// <summary>The client id and the slug, which are public; never the secret or the key.</summary>
    public override string ToString() => $"GitHubSettings {{ ClientId = {ClientId}, AppSlug = {AppSlug} }}";

    private static string Required(Func<string, string?> read, string name)
    {
        var value = read(name);
        return string.IsNullOrEmpty(value)
            ? throw new StartupException($"{name}: is not set, and Dev flow with DEVFLOW_CODE_HOST=github reads GitHub as its app with it")
            : value;
    }

    /// <summary>
    /// The RSA private key in the file, in either form GitHub and openssl write one in, PKCS #1
    /// (<c>BEGIN RSA PRIVATE KEY</c>, what GitHub gives) or PKCS #8 (<c>BEGIN PRIVATE KEY</c>).
    /// A public key, a key of another kind, an encrypted one or one shorter than 2048 bits is
    /// refused. No exception's message is kept, so nothing of the file reaches the refusal.
    /// </summary>
    private static RSA ReadKey(string path)
    {
        const string name = PrivateKeyFileSetting;
        string pem;
        try
        {
            pem = File.ReadAllText(path);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new StartupException($"{name}: names no file Dev flow can read; it is the path of the app's private key, which the app's GitHub page makes");
        }

        if (!PemEncoding.TryFind(pem, out var fields)
            || pem[fields.Label] is not ("RSA PRIVATE KEY" or "PRIVATE KEY"))
        {
            throw new StartupException($"{name}: the file holds no RSA private key in PEM, which begins -----BEGIN RSA PRIVATE KEY-----");
        }

        var key = RSA.Create();
        try
        {
            key.ImportFromPem(pem.AsSpan(fields.Location));
        }
        catch (Exception refused) when (refused is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new StartupException($"{name}: the file holds no RSA private key in PEM that can be read, which begins -----BEGIN RSA PRIVATE KEY-----");
        }

        if (key.KeySize < MinimumKeyBits)
        {
            key.Dispose();
            throw new StartupException($"{name}: the key is shorter than {MinimumKeyBits} bits, the size GitHub makes");
        }

        return key;
    }
}