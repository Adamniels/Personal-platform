using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using NSec.Cryptography;

namespace PlatformAuth.Tests;

/// <summary>The contract fixtures in contracts/token/, read from the repository root.</summary>
internal static class Contract
{
    public static string Read(string file) => File.ReadAllText(Path.Combine(TokenFolder(), file));

    public static string Token => Read("token.jwt");

    public static JsonObject ExpectedClaims => JsonNode.Parse(Read("claims.json"))!.AsObject();

    public static string Jwks => Read("jwks.json");

    /// <summary>The fixture's one key, by its key id.</summary>
    public static Dictionary<string, PublicKey> Keys()
    {
        var jwk = JsonNode.Parse(Jwks)!["keys"]![0]!;
        var raw = Base64Url.DecodeFromChars((string)jwk["x"]!);
        var key = PublicKey.Import(SignatureAlgorithm.Ed25519, raw, KeyBlobFormat.RawPublicKey);
        return new() { [(string)jwk["kid"]!] = key };
    }

    // Walks up from the test binary to the folder holding contracts/token/, so the tests run from
    // any working directory.
    private static string TokenFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "token");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("contracts/token/ not found above the test binary");
    }
}

/// <summary>Tokens built by hand, so a test controls every byte of them.</summary>
internal static class Tokens
{
    // Long past, so anything consulting the system clock rather than the time passed in would find
    // every token here expired.
    public const long Now = 1_000_000_000;
    public const long Lifetime = 1800;
    public const string Kid = "trusted";

    public static Key NewKey() =>
        Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    public static string B64(byte[] data) => Base64Url.EncodeToString(data);

    public static byte[] UnB64(string text) => Base64Url.DecodeFromChars(text);

    public static string Segment(JsonNode value) => B64(Encoding.UTF8.GetBytes(value.ToJsonString()));

    /// <summary>The claims the core writes, with each entry in <paramref name="changes"/> applied
    /// and a null value removing the claim.</summary>
    public static JsonObject Wire(long now = Now, params (string Name, JsonNode? Value)[] changes)
    {
        var claims = new JsonObject
        {
            ["sub"] = "01900000-0000-7000-8000-00000000000a",
            ["sid"] = "01900000-0000-7000-8000-00000000000b",
            ["jti"] = "01900000-0000-7000-8000-00000000000c",
            ["iat"] = now,
            ["exp"] = now + Lifetime,
        };
        foreach (var (name, value) in changes)
        {
            if (value is null)
            {
                claims.Remove(name);
            }
            else
            {
                claims[name] = value;
            }
        }

        return claims;
    }

    public static JsonObject Header(string alg = "EdDSA", string kid = Kid) =>
        new() { ["typ"] = "JWT", ["alg"] = alg, ["kid"] = kid };

    public static string Signed(Key key, JsonNode header, JsonNode payload) =>
        SignedRaw(key, Segment(header), Segment(payload));

    public static string SignedRaw(Key key, string header, string payload)
    {
        var signingInput = $"{header}.{payload}";
        var signature = SignatureAlgorithm.Ed25519.Sign(key, Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{B64(signature)}";
    }

    public static string Mint(Key key, JsonObject? payload = null, string kid = Kid) =>
        Signed(key, Header(kid: kid), payload ?? Wire());

    public static string RawPublic(Key key) => B64(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    public static JsonObject Jwk(Key key, string kid) =>
        new() { ["kty"] = "OKP", ["crv"] = "Ed25519", ["alg"] = "EdDSA", ["kid"] = kid, ["x"] = RawPublic(key) };

    public static string Jwks(Key key, string kid) =>
        new JsonObject { ["keys"] = new JsonArray(Jwk(key, kid)) }.ToJsonString();

    public static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement;
}