using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using NSec.Cryptography;

using static PlatformAuth.Tests.Tokens;

namespace PlatformAuth.Tests;

/// <summary>
/// <see cref="TokenVerifier.Verify"/>, against the token the core produced and against tokens
/// built by hand for each way verification is known to break, listed in "A feature's
/// authentication tests are part of the feature template" in High-level/docs/how-we-build.md.
/// The C# twin of libs/python/platform-auth/tests/test_verify.py.
/// </summary>
public sealed class VerifyTests : IDisposable
{
    private readonly Key _signingKey = NewKey();
    private readonly Dictionary<string, PublicKey> _keys;

    public VerifyTests()
    {
        _keys = new() { [Kid] = _signingKey.PublicKey };
    }

    public void Dispose() => _signingKey.Dispose();

    private static Refused RefusedBy(string token, IReadOnlyDictionary<string, PublicKey> keys, long now = Now) =>
        Assert.Throws<Refused>(() => TokenVerifier.Verify(token, keys, now));

    private void AssertInvalid(string token, string? because = null)
    {
        var refused = RefusedBy(token, _keys);
        Assert.True(refused.Reason == RefusalReason.Invalid, because ?? token);
    }

    [Fact]
    public void TheFixtureTokenVerifiesAtIssueAndAtExpiryPlusFive()
    {
        var expected = Contract.ExpectedClaims;
        var iat = (long)expected["iat"]!;
        var exp = (long)expected["exp"]!;
        var claims = new Claims(
            AccountId: Guid.Parse((string)expected["sub"]!),
            FamilyId: Guid.Parse((string)expected["sid"]!),
            TokenId: Guid.Parse((string)expected["jti"]!),
            IssuedAt: iat,
            ExpiresAt: exp,
            KeyId: (string)expected["kid"]!);

        Assert.Equal(5, TokenVerifier.LeewaySeconds);
        Assert.Equal(claims, TokenVerifier.Verify(Contract.Token, Contract.Keys(), iat));
        Assert.Equal(claims, TokenVerifier.Verify(Contract.Token, Contract.Keys(), exp));
        Assert.Equal(claims, TokenVerifier.Verify(Contract.Token, Contract.Keys(), exp + 5));
    }

    [Fact]
    public void TheFixtureTokenIsRefusedAsExpiredAtExpiryPlusSix()
    {
        var exp = (long)Contract.ExpectedClaims["exp"]!;

        Assert.Equal(RefusalReason.Expired, RefusedBy(Contract.Token, Contract.Keys(), exp + 6).Reason);
    }

    [Fact]
    public void AlgorithmNoneIsRefused()
    {
        var parts = Mint(_signingKey).Split('.');

        foreach (var alg in new[] { "none", "None", "NONE" })
        {
            var header = Segment(Header(alg: alg));
            AssertInvalid($"{header}.{parts[1]}.{parts[2]}", alg);
            AssertInvalid($"{header}.{parts[1]}.", alg);
        }
    }

    [Fact]
    public void EveryAlgorithmButEdDsaIsRefused()
    {
        // Signed with the real key, so the algorithm is the only thing wrong.
        Assert.NotNull(TokenVerifier.Verify(Signed(_signingKey, Header(), Wire()), _keys, Now));

        foreach (var alg in new[] { "HS256", "HS384", "HS512", "RS256", "PS256", "ES256", "eddsa", "Ed25519", "" })
        {
            AssertInvalid(Signed(_signingKey, Header(alg: alg), Wire()), alg);
        }
    }

    [Fact]
    public void HS256KeyedWithThePublicKeyIsRefused()
    {
        var raw = _signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var pem = _signingKey.PublicKey.Export(KeyBlobFormat.PkixPublicKeyText);

        // Each form a verifier might hold the key in.
        foreach (var secret in new[] { raw, Encoding.ASCII.GetBytes(B64(raw)), pem })
        {
            var signingInput = $"{Segment(Header(alg: "HS256"))}.{Segment(Wire())}";
            var mac = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(signingInput));

            AssertInvalid($"{signingInput}.{B64(mac)}");
        }
    }

    [Fact]
    public void ABadSignatureIsRefused()
    {
        var parts = Mint(_signingKey).Split('.');

        // The control: the token as minted verifies.
        Assert.NotNull(TokenVerifier.Verify(string.Join('.', parts), _keys, Now));

        var alteredPayload = Segment(Wire(changes: ("sub", "01900000-0000-7000-8000-0000000000ff")));
        AssertInvalid($"{parts[0]}.{alteredPayload}.{parts[2]}");

        var flipped = UnB64(parts[2]);
        flipped[0] ^= 1;
        AssertInvalid($"{parts[0]}.{parts[1]}.{B64(flipped)}");

        // Signed by another key, under the trusted key's id.
        using var other = NewKey();
        AssertInvalid(Mint(other, kid: Kid));
    }

    [Fact]
    public void AnUnknownKeyIdIsRefused()
    {
        AssertInvalid(Mint(_signingKey, kid: "unknown"));
        AssertInvalid(Signed(_signingKey, new JsonObject { ["alg"] = "EdDSA" }, Wire()));
        foreach (var kid in new JsonNode?[] { null, 7, "", new JsonArray("trusted") })
        {
            AssertInvalid(Signed(_signingKey, new JsonObject { ["alg"] = "EdDSA", ["kid"] = kid }, Wire()));
        }

        Assert.Equal(
            RefusalReason.Invalid,
            RefusedBy(Mint(_signingKey), new Dictionary<string, PublicKey>()).Reason);
    }

    [Fact]
    public void AMissingClaimIsRefused()
    {
        foreach (var claim in new[] { "sub", "sid", "jti", "iat", "exp" })
        {
            AssertInvalid(Mint(_signingKey, Wire(changes: (claim, null))), claim);
        }
    }

    [Fact]
    public void ANonUuidSubjectIsRefused()
    {
        foreach (var (name, value) in new (string, JsonNode)[]
        {
            ("sub", "not-a-uuid"),
            ("sid", "not-a-uuid"),
            ("jti", "not-a-uuid"),
            ("sub", ""),
            ("sub", 7),
            ("sub", "{01900000-0000-7000-8000-00000000000a}"),
            ("sub", "019000000000700080000000000000000a"),
            ("sub", "01900000000070008000000000000000"),
            ("sub", "01900000-0000-7000-8000-00000000000A"),
            ("sub", " 01900000-0000-7000-8000-00000000000a"),
            ("sub", "01900000-0000-7000-8000-00000000000a\n"),
        })
        {
            AssertInvalid(Mint(_signingKey, Wire(changes: (name, value))), $"{name}={value.ToJsonString()}");
        }
    }

    [Fact]
    public void ANonIntegerTimeIsRefused()
    {
        foreach (var (name, value) in new (string, JsonNode)[]
        {
            ("exp", (Now + Lifetime).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("exp", Now + Lifetime + 0.5),
            ("exp", true),
            ("iat", "now"),
            ("exp", JsonNode.Parse("1.0e9")!),
        })
        {
            AssertInvalid(Mint(_signingKey, Wire(changes: (name, value))), $"{name}={value.ToJsonString()}");
        }
    }

    [Fact]
    public void AMalformedTokenIsRefused()
    {
        foreach (var token in new[] { "", "a", "a.b", "a.b.c", "..", "x.y.z.w", "é.é.é", " " })
        {
            AssertInvalid(token);
        }

        // A header or payload that is not a JSON object, signed so only its shape is wrong.
        var header = Segment(Header());
        foreach (var body in new[] { "[]", "7", "not json" })
        {
            AssertInvalid(SignedRaw(_signingKey, header, B64(Encoding.UTF8.GetBytes(body))), body);
            AssertInvalid(SignedRaw(_signingKey, B64(Encoding.UTF8.GetBytes(body)), Segment(Wire())), body);
        }

        // base64url only: no padding, and no characters from standard base64.
        var parts = Mint(_signingKey).Split('.');
        AssertInvalid($"{parts[0]}=.{parts[1]}.{parts[2]}");
        AssertInvalid($"{parts[0]}.{parts[1]}.{parts[2].Replace('-', '+').Replace('_', '/')}x");

        // A claim given twice is not a claim the core wrote.
        var twice = B64(Encoding.UTF8.GetBytes(
            "{\"sub\":\"01900000-0000-7000-8000-00000000000a\",\"sub\":\"01900000-0000-7000-8000-0000000000ff\","
            + "\"sid\":\"01900000-0000-7000-8000-00000000000b\",\"jti\":\"01900000-0000-7000-8000-00000000000c\","
            + $"\"iat\":{Now},\"exp\":{Now + Lifetime}}}"));
        AssertInvalid(SignedRaw(_signingKey, header, twice));
    }

    [Fact]
    public void EveryRefusalLooksTheSameToACaller()
    {
        var expired = RefusedBy(Mint(_signingKey), _keys, Now + Lifetime + 6);
        var invalid = RefusedBy("a.b.c", _keys);

        Assert.Equal((RefusalReason.Expired, RefusalReason.Invalid), (expired.Reason, invalid.Reason));
        Assert.Equal(expired.Message, invalid.Message);
        Assert.Equal(typeof(Refused), expired.GetType());
        Assert.Equal(typeof(Refused), invalid.GetType());
    }
}