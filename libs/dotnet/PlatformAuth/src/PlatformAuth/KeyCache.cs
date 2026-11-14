using System.Buffers.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using NSec.Cryptography;

namespace PlatformAuth;

/// <summary>
/// The core's public keys, fetched from its key set endpoint and cached in memory, by the rule in
/// "How a feature authenticates" in <c>High-level/docs/feature-contract.md</c>: a key id the cache
/// does not know means fetching the set once more before failing, which is what lets the core
/// rotate keys, but at most once every thirty seconds, so a stream of made up key ids cannot turn
/// every request into a call to the core. A failed fetch keeps the keys it had, so a service keeps
/// verifying while the core is down. The C# twin of platform-auth's <c>keys.py</c>.
/// </summary>
public sealed partial class KeyCache : IDisposable
{
    /// <summary>
    /// Served by the core, public and without a session. See "The set is served at GET
    /// /.well-known/jwks.json" in <c>Core/docs/core-architecture.md</c>.
    /// </summary>
    public const string JwksPath = "/.well-known/jwks.json";

    public static readonly TimeSpan RefetchCooldown = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _client;
    private readonly Uri _url;
    private readonly TimeProvider _time;

    // One fetch at a time; lookups of a cached key never wait on it.
    private readonly SemaphoreSlim _fetching = new(1, 1);

    // Replaced whole by a successful fetch, never changed in place, so a reader needs no lock.
    private volatile Dictionary<string, PublicKey> _keys = [];
    private DateTimeOffset? _lastFetch;

    /// <param name="client">Makes the requests, so tests can answer them from memory.</param>
    /// <param name="coreUrl">The core's own address, never the proxy's.</param>
    /// <param name="timeProvider">The clock the cooldown and the timeout are measured on.</param>
    public KeyCache(HttpClient client, Uri coreUrl, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(coreUrl);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _client = client;
        _url = new Uri(coreUrl.AbsoluteUri.TrimEnd('/') + JwksPath);
        _time = timeProvider;
    }

    /// <summary>
    /// The key with <paramref name="keyId"/>, fetching the set first if it is not cached and the
    /// last fetch, successful or not, was not inside the cooldown. Null if the set, as last
    /// fetched, does not hold it.
    /// </summary>
    public async Task<PublicKey?> FindAsync(string keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (_keys.TryGetValue(keyId, out var key))
        {
            return key;
        }

        await _fetching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A fetch that ran while this one waited may have brought it.
            if (_keys.TryGetValue(keyId, out key))
            {
                return key;
            }

            var now = _time.GetUtcNow();
            if (_lastFetch is { } last && now - last < RefetchCooldown)
            {
                return null;
            }

            _lastFetch = now;
            if (await FetchAsync(cancellationToken).ConfigureAwait(false) is { } fetched)
            {
                _keys = fetched;
            }

            return _keys.GetValueOrDefault(keyId);
        }
        finally
        {
            _fetching.Release();
        }
    }

    /// <summary>
    /// The Ed25519 keys in a JWK set (RFC 7517 and RFC 8037), by key id, or null if
    /// <paramref name="body"/> is not a set. Entries of any other kind, and entries that do not
    /// parse, are ignored.
    /// </summary>
    public static Dictionary<string, PublicKey>? ParseKeySet(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("keys", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var keys = new Dictionary<string, PublicKey>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || Text(entry, "kty") != "OKP"
                || Text(entry, "crv") != "Ed25519"
                || Text(entry, "kid") is not { } kid
                || Text(entry, "x") is not { } x
                || PublicKeyFrom(x) is not { } key)
            {
                continue;
            }

            keys[kid] = key;
        }

        return keys;
    }

    public void Dispose() => _fetching.Dispose();

    // The set the core serves now, or null if it could not be had: the network, the timeout, a
    // status other than 200, or a body that is not a key set. Cancellation by the caller is not a
    // failed fetch and is passed on.
    private async Task<Dictionary<string, PublicKey>?> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(FetchTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await _client.GetAsync(_url, linked.Token).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return null;
            }

            var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var json = await JsonDocument.ParseAsync(body, default, linked.Token).ConfigureAwait(false);
                return ParseKeySet(json.RootElement);
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static PublicKey? PublicKeyFrom(string x)
    {
        // base64url without padding, as RFC 7515 writes it, and exactly a key's 32 bytes.
        if (!Base64UrlForm().IsMatch(x) || x.Length % 4 == 1)
        {
            return null;
        }

        try
        {
            var raw = Base64Url.DecodeFromChars(x);
            return PublicKey.TryImport(SignatureAlgorithm.Ed25519, raw, KeyBlobFormat.RawPublicKey, out var key)
                ? key
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex Base64UrlForm();
}