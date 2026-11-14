namespace PlatformAuth;

/// <summary>
/// What a verified access token says. <see cref="AccountId"/> is the user id, always. Times are
/// whole seconds since the epoch, as the core writes them.
/// </summary>
public sealed record Claims(
    Guid AccountId,
    Guid FamilyId,
    Guid TokenId,
    long IssuedAt,
    long ExpiresAt,
    string KeyId);