using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// Signing in as the app: a JSON Web Token signed with RS256 by the app's private key, <c>iss</c>
/// its client id, <c>iat</c> 60 seconds in the past, against a clock on GitHub's side that runs
/// behind, and <c>exp</c> 9 minutes ahead, inside GitHub's limit of 10. It only asks GitHub for
/// installation tokens, is made when needed and never stored. RS256 is .NET's own RSA, with no
/// JWT library. See "GitHub" in devflow/docs/devflow-architecture.md.
/// </summary>
internal static class AppToken
{
    public static readonly TimeSpan Backdate = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(9);

    public static string Make(string clientId, RSA key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        var header = Segment(writer =>
        {
            writer.WriteString("alg", "RS256");
            writer.WriteString("typ", "JWT");
        });
        var claims = Segment(writer =>
        {
            writer.WriteNumber("iat", (now - Backdate).ToUnixTimeSeconds());
            writer.WriteNumber("exp", (now + Lifetime).ToUnixTimeSeconds());
            writer.WriteString("iss", clientId);
        });
        var signingInput = $"{header}.{claims}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    /// <summary>A JSON object written by <paramref name="write"/>, base64url encoded.</summary>
    private static string Segment(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Base64Url.EncodeToString(buffer.ToArray());
    }
}