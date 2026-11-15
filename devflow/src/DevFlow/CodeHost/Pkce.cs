using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace DevFlow.CodeHost;

/// <summary>
/// Proof Key for Code Exchange, RFC 7636, with the S256 method, which GitHub strongly recommends:
/// the verifier is a random secret Dev flow keeps, and the challenge sent to the host is the
/// base64url of its SHA-256, so only whoever holds the verifier can spend the code the host sends
/// back. Also what the fake checks a verifier against.
/// </summary>
public static class Pkce
{
    /// <summary>The method's name as the host takes it.</summary>
    public const string Method = "S256";

    /// <summary>The challenge made from <paramref name="verifier"/>: base64url, without padding,
    /// of the SHA-256 of its ASCII bytes.</summary>
    public static string Challenge(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>Whether <paramref name="challenge"/> was made from <paramref name="verifier"/>,
    /// compared in fixed time.</summary>
    public static bool Matches(string challenge, string verifier)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Challenge(verifier)), Encoding.ASCII.GetBytes(challenge));
    }

    /// <summary>A random secret of <paramref name="bytes"/> bytes, base64url without padding: 32
    /// bytes is 43 characters, the shortest verifier RFC 7636 allows.</summary>
    public static string Random(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
}