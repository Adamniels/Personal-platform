using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using NSec.Cryptography;

namespace PlatformAuth;

/// <summary>
/// Verifying the core's access tokens: EdDSA only, the key chosen by the token's key id, and
/// expiry checked here against a time passed in, with the leeway chosen rather than inherited.
/// The C# twin of <c>Core/src/verifier.rs</c> and of platform-auth's <c>verify.py</c>; the rules
/// are in "How a feature authenticates" in <c>High-level/docs/feature-contract.md</c> and "The
/// clock leeway on verification is chosen, not inherited" in <c>Core/docs/core-architecture.md</c>.
///
/// The JWS compact form is parsed here rather than by a JWT library, since every rule it applies
/// is the contract's and a library's defaults (issuer, audience, lifetime, leeway) would each have
/// to be switched off. Only the signature is cryptography, and that is NSec's, over libsodium.
/// </summary>
public static partial class TokenVerifier
{
    /// <summary>
    /// How long past its <c>exp</c> a token is still accepted, to absorb clock drift between
    /// machines. The same five seconds in every verifier.
    /// </summary>
    public const int LeewaySeconds = 5;

    private const string Algorithm = "EdDSA";

    // A claim given twice is not a token the core wrote, so it is refused rather than one copy read.
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// The claims of <paramref name="token"/> if it is valid at <paramref name="now"/>, whole
    /// seconds since the epoch: signed with EdDSA by the key its header names in
    /// <paramref name="keys"/>, and <paramref name="now"/> no later than its <c>exp</c> plus the
    /// leeway. Nothing here reads the clock.
    /// </summary>
    /// <exception cref="Refused">For every token that is not accepted, whatever the reason.</exception>
    public static Claims Verify(string token, IReadOnlyDictionary<string, PublicKey> keys, long now)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(keys);

        var (header, payload, signature) = Split(token);

        string keyId;
        using (var headerJson = Parse(Decode(header)))
        {
            var root = headerJson.RootElement;
            if (!root.TryGetProperty("alg", out var alg)
                || alg.ValueKind != JsonValueKind.String
                || alg.GetString() != Algorithm)
            {
                throw Invalid();
            }

            keyId = KeyId(root) ?? throw Invalid();
        }

        if (!keys.TryGetValue(keyId, out var key))
        {
            throw Invalid();
        }

        // The signature first: nothing in the payload is read until the core is known to have
        // written it.
        var signingInput = Encoding.ASCII.GetBytes(token, 0, header.Length + 1 + payload.Length);
        if (!SignatureAlgorithm.Ed25519.Verify(key, signingInput, Decode(signature)))
        {
            throw Invalid();
        }

        using var payloadJson = Parse(Decode(payload));
        var claims = payloadJson.RootElement;
        var verified = new Claims(
            AccountId: Uuid(claims, "sub"),
            FamilyId: Uuid(claims, "sid"),
            TokenId: Uuid(claims, "jti"),
            IssuedAt: Seconds(claims, "iat"),
            ExpiresAt: Seconds(claims, "exp"),
            KeyId: keyId);

        if (verified.ExpiresAt + LeewaySeconds < now)
        {
            throw new Refused(RefusalReason.Expired);
        }

        return verified;
    }

    /// <summary>
    /// The <c>kid</c> in the token's header, read without verifying anything, so the edge knows
    /// which key to fetch. Null when there is none to read. It is not a second rule:
    /// <see cref="Verify"/> makes the same check and is the one that decides.
    /// </summary>
    internal static string? ReadKeyId(string token)
    {
        try
        {
            var (header, _, _) = Split(token);
            using var json = Parse(Decode(header));
            return KeyId(json.RootElement);
        }
        catch (Refused)
        {
            return null;
        }
    }

    private static string? KeyId(JsonElement header) =>
        header.TryGetProperty("kid", out var kid) && kid.ValueKind == JsonValueKind.String
            ? kid.GetString()
            : null;

    // Exactly three segments, each base64url without padding, as RFC 7515's compact form writes
    // them. The signature segment may not be empty.
    private static (string Header, string Payload, string Signature) Split(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            throw Invalid();
        }

        foreach (var part in parts)
        {
            if (!Base64UrlForm().IsMatch(part))
            {
                throw Invalid();
            }
        }

        return (parts[0], parts[1], parts[2]);
    }

    private static byte[] Decode(string segment)
    {
        // A length that leaves one character over is not base64 at all.
        if (segment.Length % 4 == 1)
        {
            throw Invalid();
        }

        try
        {
            return Base64Url.DecodeFromChars(segment);
        }
        catch (FormatException)
        {
            throw Invalid();
        }
    }

    // A JSON object, or a refusal.
    private static JsonDocument Parse(byte[] utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, Strict);
        }
        catch (JsonException)
        {
            throw Invalid();
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw Invalid();
        }

        return document;
    }

    // In the form the core writes every id: lowercase, hyphenated, nothing around it. Guid.Parse
    // alone also takes braces, uppercase and no hyphens, which the core never sends.
    private static Guid Uuid(JsonElement claims, string name)
    {
        if (!claims.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || !UuidForm().IsMatch(text))
        {
            throw Invalid();
        }

        return Guid.ParseExact(text, "D");
    }

    // An integer, as the core writes it: not a string, a bool, a fraction or an exponent.
    private static long Seconds(JsonElement claims, string name)
    {
        if (!claims.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !IntegerForm().IsMatch(value.GetRawText())
            || !value.TryGetInt64(out var seconds))
        {
            throw Invalid();
        }

        return seconds;
    }

    private static Refused Invalid() => new(RefusalReason.Invalid);

    [GeneratedRegex(@"^[A-Za-z0-9_-]*\z")]
    private static partial Regex Base64UrlForm();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex UuidForm();

    [GeneratedRegex(@"^-?[0-9]+\z")]
    private static partial Regex IntegerForm();
}