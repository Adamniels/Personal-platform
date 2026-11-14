using System.Diagnostics.CodeAnalysis;

namespace PlatformAuth;

/// <summary>Why a token was refused. For logs and tests, never for a response.</summary>
public enum RefusalReason
{
    /// <summary>
    /// Not a token, the wrong algorithm, a missing or unknown key id, a bad signature, or claims
    /// missing or malformed.
    /// </summary>
    Invalid,

    /// <summary>Signed correctly, and more than the leeway past its <c>exp</c>.</summary>
    Expired,
}

/// <summary>
/// A token that is not accepted. Every refusal is handled the same way, so its message is the
/// same whatever the reason, and <see cref="Reason"/> is for logs and tests.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "The name is the contract's word, the same as platform-auth's Refused in Python.")]
public sealed class Refused(RefusalReason reason) : Exception("token refused")
{
    public RefusalReason Reason { get; } = reason;
}