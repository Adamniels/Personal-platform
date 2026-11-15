using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DevFlow;

/// <summary>
/// One of the vault's AES-256 keys, 32 bytes, and the id a sealed row records it by. Written in a
/// setting as <c>&lt;id&gt;:&lt;base64 of 32 bytes&gt;</c>, see "The vault" in devflow/README.md.
/// It never prints its bytes: <see cref="ToString"/> names the id alone, and a key that does not
/// parse is refused without repeating it.
/// </summary>
public sealed partial class VaultKey
{
    /// <summary>The bytes of an AES-256 key.</summary>
    public const int Length = 32;

    /// <summary>The characters of the padded base64 of <see cref="Length"/> bytes, as
    /// <c>openssl rand -base64 32</c> prints them.</summary>
    private const int EncodedLength = 44;

    private readonly byte[] _bytes;

    private VaultKey(string id, byte[] bytes)
    {
        Id = id;
        _bytes = bytes;
    }

    /// <summary>What a sealed row names the key by, 1 to 16 lowercase ASCII letters and digits.</summary>
    public string Id { get; }

    /// <summary>The key itself, for the cipher and nothing else.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>A new random key under <paramref name="id"/>.</summary>
    public static VaultKey Generate(string id)
    {
        if (!IsId(id))
        {
            throw new ArgumentException("is not 1 to 16 lowercase letters and digits", nameof(id));
        }

        return new VaultKey(id, RandomNumberGenerator.GetBytes(Length));
    }

    /// <summary>
    /// <c>&lt;id&gt;:&lt;base64&gt;</c>: the id, 1 to 16 lowercase ASCII letters and digits, a colon,
    /// and exactly the padded standard base64 of 32 bytes, nothing around it.
    /// </summary>
    /// <exception cref="FormatException">It is anything else. The message never repeats
    /// <paramref name="text"/>, which holds the key.</exception>
    internal static VaultKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !IsId(text[..colon]))
        {
            throw new FormatException(
                "is not <id>:<base64 of 32 bytes>, with an id of 1 to 16 lowercase letters and digits");
        }

        // Convert would skip whitespace inside, so the length and the alphabet are checked first.
        var encoded = text[(colon + 1)..];
        var bytes = new byte[Length];
        if (encoded.Length != EncodedLength
            || !encoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')
            || !Convert.TryFromBase64String(encoded, bytes, out var written)
            || written != Length)
        {
            throw new FormatException(
                "is not <id>:<base64 of 32 bytes>: the key is not the padded base64 of exactly 32 bytes, as openssl rand -base64 32 prints it");
        }

        return new VaultKey(text[..colon], bytes);
    }

    /// <summary>The id alone, never the key.</summary>
    public override string ToString() => $"VaultKey {{ Id = {Id} }}";

    internal static bool IsId(string id) => KeyId().IsMatch(id);

    /// <summary>Ending at <c>\z</c>, since <c>$</c> would let a trailing newline through.</summary>
    [GeneratedRegex(@"^[a-z0-9]{1,16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyId();
}